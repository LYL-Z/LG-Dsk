using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.UI;

/// <summary>Both native scene providers run outside the application on every Windows build.</summary>
public static class BackdropCaptureWorker
{
    internal const int ProtocolMagic = 0x47444333;
    internal const int ProtocolVersion = 3;
    public static bool RunIfRequested(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length != 2 || arguments[0] != "--glass-backdrop-worker") return false;
        if (!int.TryParse(arguments[1], out var parentId) || parentId <= 0) { exitCode = 2; return true; }
        using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        try
        {
            using var parent = Process.GetProcessById(parentId);
            // Input can block forever after a parent crash. A watchdog owns that lifetime,
            // even when the capture thread is blocked in a driver or native destructor.
            using var lifetime = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (parent.HasExited) Environment.Exit(0);
                    try { await Task.Delay(500, lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });
            SetThreadDpiAwarenessContext(-4);
            if (input.ReadInt32() != ProtocolMagic || input.ReadInt32() != ProtocolVersion)
                throw new InvalidDataException("Invalid capture protocol.");
            var provider = input.ReadByte();
            if (provider > 2) throw new InvalidDataException("Invalid capture provider.");
            var region = new ScreenRect(input.ReadInt32(), input.ReadInt32(), input.ReadInt32(), input.ReadInt32());
            if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
                throw new InvalidDataException("Invalid capture extent.");
            using var pixels = SharedCapturePixels.Open(input.ReadString(), checked(region.Width * region.Height * 4));
            var excluded = ReadFamily(input, parentId);
            using IDesktopSceneCapture capture = provider switch
            {
                0 => new SharedDesktopSceneCapture(region, isolatedParentProcess: parentId, excludedProcesses: () => excluded),
                1 => new DocumentedDesktopSceneCapture(region, excludedProcesses: () => excluded),
                _ => new WallpaperSceneCapture(region)
            };
            output.Write((byte)1); output.Write(capture.CarrierHandle.ToInt64()); output.Flush();
            while (!parent.HasExited)
            {
                var command = input.ReadByte();
                if (command == 0) break;
                var sequence = input.ReadInt64(); var generation = input.ReadInt64();
                if (sequence <= 0 || generation <= 0) throw new InvalidDataException("Invalid capture request identity.");
                excluded = ReadFamily(input, parentId);
                if (command == 3)
                {
                    output.Write(capture.UpdateExclusions() ? (byte)1 : (byte)0);
                    output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); output.Flush(); continue;
                }
                if (command == 2)
                {
                    capture.ReleaseReadbackBuffers(); output.Write((byte)0);
                    output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); output.Flush(); continue;
                }
                if (command != 1) throw new InvalidDataException("Unknown capture command.");
                var notBefore = input.ReadInt64();
                var wait = Math.Clamp(input.ReadInt32(), 0, 100);
                var frame = capture.TryRead(TimeSpan.FromMilliseconds(wait), sourceNotBeforeTicks: notBefore, reusePixels: true);
                if (frame is null)
                { output.Write((byte)0); output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); }
                else
                {
                    pixels.Write(frame.Pixels);
                    output.Write((byte)1); output.Write(sequence); output.Write(generation);
                    output.Write(capture.SceneRevision);
                    output.Write(frame.Width); output.Write(frame.Height);
                    output.Write(frame.SourceQpcTicks); output.Write(frame.Pixels.Length);
                }
                output.Flush();
                global::Program.Pump(TimeSpan.FromMilliseconds(1));
            }
            lifetime.Cancel();
        }
        catch (EndOfStreamException) { }
        catch (IOException) { exitCode = 1; }
        catch (Exception error)
        {
            exitCode = 1;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(error.Message);
                if (bytes.Length > 8192) bytes = bytes[..8192];
                output.Write((byte)2); output.Write(bytes.Length); output.Write(bytes); output.Flush();
            }
            catch (IOException) { }
        }
        return true;
    }
    private static int[] ReadFamily(BinaryReader input, int parent)
    {
        var count = input.ReadInt32();
        if (count < 1 || count > 256) throw new InvalidDataException("Invalid exclusion family.");
        var family = new HashSet<int> { Environment.ProcessId, parent };
        for (var i = 0; i < count; i++)
        {
            var pid = input.ReadInt32();
            if (pid <= 0) throw new InvalidDataException("Invalid exclusion process.");
            family.Add(pid);
        }
        return family.ToArray();
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

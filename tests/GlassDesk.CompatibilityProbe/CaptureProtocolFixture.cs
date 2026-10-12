using System.IO;
using System.Text;
using System.Diagnostics;

// Deliberately broken peers exercise the real process/pipe/mapping boundary.
internal static class CaptureProtocolFixture
{
    internal static int Run(string fault)
    {
        if (Environment.GetEnvironmentVariable("GLASSDESK_PROTOCOL_PID_FILE") is { } path)
            File.WriteAllText(path, Environment.ProcessId.ToString());
        using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        input.ReadInt32(); input.ReadInt32(); input.ReadBoolean();
        input.ReadInt32(); input.ReadInt32(); var width = input.ReadInt32(); var height = input.ReadInt32();
        var name = input.ReadString(); Family(input);
        if (fault == "initial-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        using var pixels = SharedCapturePixels.Open(name, width * height * 4);
        output.Write((byte)1); output.Write(0L); output.Flush();
        input.ReadByte(); var sequence = input.ReadInt64(); var generation = input.ReadInt64(); Family(input);
        var barrier = input.ReadInt64(); input.ReadInt32();
        if (fault == "read-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        output.Write((byte)1); output.Write(fault == "old-sequence" ? sequence - 1 : sequence);
        output.Write(fault == "old-generation" ? generation - 1 : generation); output.Write(1L); output.Flush();
        if (fault == "split-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        pixels.Write(new byte[width * height * 4]);
        output.Write(width); output.Write(height);
        output.Write(fault == "old-source" ? barrier - 1 : Stopwatch.GetTimestamp()); output.Write(width * height * 4); output.Flush();
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static void Family(BinaryReader input)
    { var count = input.ReadInt32(); for (var i = 0; i < count; i++) input.ReadInt32(); }
}

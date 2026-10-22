using System.IO;
using GlassDesk.Core.Storage;
using GlassDesk.Acceptance;

internal static partial class Program
{
    private const string IsolatedDesktopEnvironmentVariable = "GLASSDESK_ISOLATED_DESKTOP";

    private static bool TryRequireIsolatedFullHostPackage(out string directory, out string refusal)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        refusal = string.Empty;

        var marker = Path.Combine(directory, ".full-host-acceptance-root");
        if (!File.Exists(marker))
        {
            refusal = "缺少与程序目录相符的 .full-host-acceptance-root 标记";
            return false;
        }
        try
        {
            var markedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(File.ReadAllText(marker).Trim()));
            if (!PathEquals(markedDirectory, directory))
            {
                refusal = "缺少与程序目录相符的 .full-host-acceptance-root 标记";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            refusal = "无法安全读取隔离验收标记：" + ex.Message;
            return false;
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(IsolatedDesktopEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            refusal = $"完整宿主验收会显示窗口或操作系统桌面；必须在隔离桌面中设置 {IsolatedDesktopEnvironmentVariable}=1";
            return false;
        }

        if (!DesktopIsolationGuard.TryReadActiveDesktopNames(out var currentDesktop, out var inputDesktop))
        {
            refusal = "无法读取当前 Win32 桌面和输入桌面；完整宿主验收已拒绝。";
            return false;
        }

        if (!DesktopIsolationGuard.IsNamedActiveAcceptanceDesktop(currentDesktop, inputDesktop))
        {
            refusal = $"完整宿主验收只允许在活动的 {DesktopIsolationGuard.DesktopNamePrefix}… 输入桌面运行；当前桌面='{currentDesktop}'，输入桌面='{inputDesktop}'。仅设置环境变量不足以启用验收。";
            return false;
        }

        if (!AppPaths.IsPortableDeployment(directory))
        {
            refusal = "隔离验收包缺少 portable.flag，拒绝使用用户级 LG Dsk 数据目录";
            return false;
        }

        var packageData = Path.GetFullPath(Path.Combine(directory, "Data"));
        if (!Directory.Exists(packageData))
        {
            refusal = "隔离验收包缺少复制后的 Data 配置目录";
            return false;
        }

        var resolvedData = AppPaths.ForCurrentUser().RootDirectory;
        if (!PathEquals(packageData, resolvedData))
        {
            refusal = $"当前数据目录不在隔离包内（预期 {packageData}；实际 {resolvedData}）";
            return false;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            refusal = "Windows 未提供 LocalAppData，无法确认验收数据与用户数据隔离";
            return false;
        }

        var userData = Path.Combine(localAppData, AppPaths.ProductFolderName, "Data");
        if (DataDirectoryGuard.Overlaps(packageData, userData))
        {
            refusal = "隔离验收包的 Data 与当前用户的 LG Dsk 数据目录重叠";
            return false;
        }

        if (HasReparsePointOnPath(directory) || TreeContainsReparsePoint(packageData))
        {
            refusal = "隔离验收包或 Data 路径包含重解析点，无法保证数据边界";
            return false;
        }

        return true;
    }

    private static bool HasReparsePointOnPath(string path)
    {
        var rootInfo = new DirectoryInfo(path);
        for (var current = rootInfo; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        return false;
    }

    private static bool TreeContainsReparsePoint(string root)
    {
        if (HasReparsePointOnPath(root)) return true;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var item in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((item.Attributes & FileAttributes.Directory) != 0)
                    pending.Push((DirectoryInfo)item);
            }
        }
        return false;
    }

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static int RefuseFullHostAcceptance(string reason)
    {
        Console.Error.WriteLine("Blocked full-host acceptance: " + reason);
        return 2;
    }
}

using System.Text;

namespace GlassDesk.AppServices;

/// <summary>安全地把文件系统路径追加为 Windows 应用命令行参数。</summary>
internal static class ApplicationFileDropArguments
{
    internal const int MaximumPathCount = 256;
    private const int MaximumArgumentLength = 30_000;

    internal static bool TryAppend(string? existingArguments, IReadOnlyList<string> paths, out string arguments)
    {
        arguments = string.Empty;
        if (paths is null || paths.Count is 0 or > MaximumPathCount) return false;

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingArguments))
        {
            if (existingArguments.Length > MaximumArgumentLength) return false;
            _ = builder.Append(existingArguments);
            if (!char.IsWhiteSpace(existingArguments[^1])) _ = builder.Append(' ');
        }

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (string.IsNullOrWhiteSpace(path)) return false;

            var quoted = QuoteArgument(path);
            var extraLength = quoted.Length + (builder.Length > 0 ? 1 : 0);
            if (builder.Length + extraLength > MaximumArgumentLength) return false;

            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1])) _ = builder.Append(' ');
            _ = builder.Append(quoted);
        }

        arguments = builder.ToString();
        return true;
    }

    /// <summary>
    /// Quotes one argument according to the Windows C runtime convention, including
    /// doubling backslashes immediately before quotes or the closing quote.
    /// </summary>
    internal static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var builder = new StringBuilder(argument.Length + 2);
        _ = builder.Append('"');
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                _ = builder.Append('\\', backslashes * 2 + 1);
                _ = builder.Append('"');
                backslashes = 0;
                continue;
            }

            _ = builder.Append('\\', backslashes);
            _ = builder.Append(character);
            backslashes = 0;
        }

        _ = builder.Append('\\', backslashes * 2);
        _ = builder.Append('"');
        return builder.ToString();
    }
}

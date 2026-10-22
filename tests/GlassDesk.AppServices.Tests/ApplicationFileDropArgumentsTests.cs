using GlassDesk.AppServices;

namespace GlassDesk.AppServices.Tests;

public sealed class ApplicationFileDropArgumentsTests
{
    [Fact]
    public void TryAppend_PreservesExistingArgumentsAndQuotesPathsWithSpaces()
    {
        var succeeded = ApplicationFileDropArguments.TryAppend(
            "--profile \"Work\"",
            [@"C:\Design Draft\final.psd", @"D:\Notes\review.txt"],
            out var arguments);

        Assert.True(succeeded);
        Assert.Equal("--profile \"Work\" \"C:\\Design Draft\\final.psd\" \"D:\\Notes\\review.txt\"", arguments);
    }

    [Fact]
    public void QuoteArgument_DoublesTrailingBackslashesBeforeClosingQuote()
    {
        var quoted = ApplicationFileDropArguments.QuoteArgument(@"C:\Folder with spaces\");

        Assert.Equal("\"C:\\Folder with spaces\\\\\"", quoted);
    }

    [Fact]
    public void QuoteArgument_EscapesEmbeddedQuotesAndTheirPrecedingBackslashes()
    {
        var quoted = ApplicationFileDropArguments.QuoteArgument("name\\\"value");

        Assert.Equal("\"name\\\\\\\"value\"", quoted);
    }

    [Fact]
    public void TryAppend_RejectsEmptyOversizedAndBlankPathLists()
    {
        Assert.False(ApplicationFileDropArguments.TryAppend(null, [], out _));
        Assert.False(ApplicationFileDropArguments.TryAppend(null, ["  "], out _));
        Assert.False(ApplicationFileDropArguments.TryAppend(
            null,
            Enumerable.Repeat("x", ApplicationFileDropArguments.MaximumPathCount + 1).ToArray(),
            out _));
        Assert.False(ApplicationFileDropArguments.TryAppend(
            null,
            [new string('x', 30_001)],
            out _));
    }
}

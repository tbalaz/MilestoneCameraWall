using GridLookout.Interop;
using Xunit;

namespace GridLookout.Tests.Interop;

/// <summary>
/// Covers <see cref="CommandLineQuoting.BuildArgumentString"/>, the T7(b)/R8 fix for the E5
/// fatal-restart relaunch's command-line quoting — see the type's own doc comment for the
/// CommandLineToArgvW rules it now implements correctly.
/// </summary>
public class CommandLineQuotingTests
{
    [Fact]
    public void PlainArgs_AreEachWrappedInQuotes()
    {
        var result = CommandLineQuoting.BuildArgumentString(new[] { "--recorder", "REC-01" });

        Assert.Equal("\"--recorder\" \"REC-01\"", result);
    }

    [Fact]
    public void EmptyArgsArray_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CommandLineQuoting.BuildArgumentString(Array.Empty<string>()));
    }

    [Fact]
    public void EmptyStringArgument_QuotesToEmptyPair()
    {
        var result = CommandLineQuoting.BuildArgumentString(new[] { "" });

        Assert.Equal("\"\"", result);
    }

    [Fact]
    public void ArgumentWithEmbeddedSpace_StaysAsOneQuotedArgument()
    {
        var result = CommandLineQuoting.BuildArgumentString(new[] { "--monitor", "living room" });

        Assert.Equal("\"--monitor\" \"living room\"", result);
    }

    [Fact]
    public void ArgumentWithEmbeddedQuote_EscapesTheQuote()
    {
        // arg: say "hi"
        var result = CommandLineQuoting.BuildArgumentString(new[] { "say \"hi\"" });

        Assert.Equal("\"say \\\"hi\\\"\"", result);
    }

    [Fact]
    public void ArgumentEndingInSingleBackslash_DoublesItBeforeClosingQuote()
    {
        // arg: C:\path\  ->  "C:\path\\"
        var result = CommandLineQuoting.BuildArgumentString(new[] { "C:\\path\\" });

        Assert.Equal("\"C:\\path\\\\\"", result);
    }

    [Fact]
    public void ArgumentWithMidStringBackslash_NotFollowedByQuote_PassesThroughUnchanged()
    {
        // arg: C:\path\to\file.txt — no trailing/pre-quote backslash run, so nothing doubles.
        var result = CommandLineQuoting.BuildArgumentString(new[] { "C:\\path\\to\\file.txt" });

        Assert.Equal("\"C:\\path\\to\\file.txt\"", result);
    }

    [Fact]
    public void BackslashImmediatelyBeforeEmbeddedQuote_DoublesThenEscapes()
    {
        // arg: a\"b -> the single backslash before the quote must double (to stay a literal
        // backslash) AND the quote itself needs its own escaping backslash: "a\\\"b"
        var result = CommandLineQuoting.BuildArgumentString(new[] { "a\\\"b" });

        Assert.Equal("\"a\\\\\\\"b\"", result);
    }

    [Fact]
    public void MultipleArguments_AreSpaceSeparated()
    {
        var result = CommandLineQuoting.BuildArgumentString(new[] { "--a", "b c", "--d" });

        Assert.Equal("\"--a\" \"b c\" \"--d\"", result);
    }

    [Fact]
    public void EvenNumberOfTrailingBackslashes_AllDoubleBeforeClosingQuote()
    {
        // arg: two trailing backslashes -> four before the closing quote.
        var result = CommandLineQuoting.BuildArgumentString(new[] { "path\\\\" });

        Assert.Equal("\"path\\\\\\\\\"", result);
    }
}

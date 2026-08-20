using System.Text;

namespace GridLookout.Interop;

/// <summary>
/// Builds a quoted command-line string for <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>
/// — used by Program.cs's E5 fatal-restart relaunch. <c>ProcessStartInfo.ArgumentList</c> isn't
/// available on net48.
///
/// WHY THIS EXISTS (T7/R8 fix). Every argument is wrapped in double quotes; backslashes are
/// escaped per the standard <c>CommandLineToArgvW</c>/MSVCRT quoting rules a Windows process uses
/// to re-parse its own command line: a run of backslashes is doubled ONLY when it immediately
/// precedes a literal quote character OR sits at the very end of the argument (immediately before
/// the closing quote this method itself appends) — backslashes anywhere else pass through
/// unchanged. The PREVIOUS implementation here (a naive <c>Replace("\"", "\\\"")</c>) got this
/// wrong: it silently corrupted or truncated an argument ending in a backslash (a Windows path is
/// the obvious case) or containing an embedded quote. GridLookout's own args never need this today
/// (<c>--recorder</c>/<c>--monitor</c>/<c>--protect-password</c> plus a simple value), but a wrong
/// general-purpose implementation sitting in the fatal-crash relaunch path is still a defect worth
/// fixing outright rather than leaving latent.
/// </summary>
public static class CommandLineQuoting
{
    public static string BuildArgumentString(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            AppendQuotedArgument(sb, arg);
        }

        return sb.ToString();
    }

    private static void AppendQuotedArgument(StringBuilder sb, string arg)
    {
        sb.Append('"');
        int backslashCount = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                // Escaping a literal quote: every preceding backslash must be doubled, plus one
                // more backslash to escape the quote itself.
                sb.Append('\\', (backslashCount * 2) + 1);
                sb.Append('"');
                backslashCount = 0;
                continue;
            }

            // An ordinary character: any pending backslashes were NOT followed by a quote, so they
            // pass through unchanged — this is what makes a lone backslash in the middle of an
            // argument (e.g. a Windows path) round-trip correctly.
            sb.Append('\\', backslashCount);
            backslashCount = 0;
            sb.Append(c);
        }

        // Trailing backslashes sit immediately before the closing quote about to be appended, so —
        // same rule as an embedded literal quote — they must be doubled, or they'd escape OUR
        // closing quote instead of terminating the argument.
        sb.Append('\\', backslashCount * 2);
        sb.Append('"');
    }
}

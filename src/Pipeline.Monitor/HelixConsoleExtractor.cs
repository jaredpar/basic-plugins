using System.Text;
using System.Text.RegularExpressions;

namespace Pipeline.Monitor;

/// <summary>
/// Extracts error-relevant lines from Helix console output to produce a compact summary
/// suitable for storage and LLM analysis. Captures stack traces, error messages,
/// crash indicators, and tail context while keeping output bounded (~8KB).
/// </summary>
public static partial class HelixConsoleExtractor
{
    private const int MaxSummaryBytes = 8 * 1024;
    private const int TailLines = 20;
    private const int ContextLinesBefore = 1;
    private const int ContextLinesAfter = 2;

    /// <summary>
    /// Extracts a summary of error-relevant lines from raw console output.
    /// Returns a filtered view with original line numbers preserved.
    /// </summary>
    public static string ExtractSummary(string consoleText)
    {
        if (string.IsNullOrWhiteSpace(consoleText))
            return "";

        var lines = consoleText.Split('\n');
        var matchedLineIndices = new HashSet<int>();

        // Pass 1: find lines matching error patterns
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsErrorLine(line))
            {
                // Add context window around the match
                for (int j = Math.Max(0, i - ContextLinesBefore); j <= Math.Min(lines.Length - 1, i + ContextLinesAfter); j++)
                {
                    matchedLineIndices.Add(j);
                }
            }
        }

        // Pass 2: expand to capture full stack trace blocks
        // If a matched line is followed by "   at ..." lines, include the whole block
        for (int i = 0; i < lines.Length; i++)
        {
            if (matchedLineIndices.Contains(i))
            {
                // Walk forward through stack trace continuation lines
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (StackTraceLine().IsMatch(lines[j]))
                        matchedLineIndices.Add(j);
                    else
                        break;
                }
            }
        }

        // Always include the tail
        for (int i = Math.Max(0, lines.Length - TailLines); i < lines.Length; i++)
        {
            matchedLineIndices.Add(i);
        }

        // Build output with line numbers, inserting "..." for gaps
        var sb = new StringBuilder();
        var sortedIndices = matchedLineIndices.OrderBy(i => i).ToList();
        int? lastIndex = null;

        foreach (var idx in sortedIndices)
        {
            if (lastIndex is not null && idx > lastIndex + 1)
            {
                sb.AppendLine("  ...");
            }

            sb.AppendLine($"{idx + 1,5}| {lines[idx].TrimEnd()}");
            lastIndex = idx;

            // Check size limit
            if (sb.Length > MaxSummaryBytes)
            {
                sb.AppendLine("  ... (truncated)");
                break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsErrorLine(string line)
    {
        return ErrorPattern().IsMatch(line);
    }

    // Matches common .NET error patterns, build errors, and crash indicators
    [GeneratedRegex(
        @"(?i)(exception|error\b|FAIL[\s:]|Assert\.|Stack Trace|" +
        @"error\s+CS\d|error\s+NU\d|error\s+MSB\d|" +
        @"Unhandled\s+exception|Segmentation\s+fault|SIGABRT|SIGSEGV|core\s+dumped|" +
        @"Process\s+(exited|terminated|crashed)|exit\s+code\s+[^0]|" +
        @"System\.\w*Exception|FAILED|fatal\s+error|" +
        @"The active test run was aborted|" +
        @"xUnit\.net\s+.*\[FAIL\])",
        RegexOptions.Compiled)]
    private static partial Regex ErrorPattern();

    // Matches stack trace continuation lines: "   at Namespace.Class.Method(...)"
    [GeneratedRegex(@"^\s+at\s+\S+", RegexOptions.Compiled)]
    private static partial Regex StackTraceLine();
}

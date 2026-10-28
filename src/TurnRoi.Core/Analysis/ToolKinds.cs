using System.Text.RegularExpressions;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Analysis;

/// <summary>
/// Classifies tool calls across agents (Claude Code, Copilot, Codex), which all name their tools differently.
/// </summary>
public static class ToolKinds
{
    private static readonly HashSet<string> ReadNames = new(StringComparer.OrdinalIgnoreCase)
        { "Read", "NotebookRead", "view", "read_file", "readFile", "copilot_readFile" };

    private static readonly HashSet<string> EditNames = new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "MultiEdit", "Write", "NotebookEdit", "apply_patch", "write_file", "edit_file" };

    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
        { "Bash", "Shell", "run_in_terminal", "shell", "local_shell", "exec_command", "container.exec" };

    private static readonly Regex TestCommand = new(
        @"\b(dotnet\s+test|npm\s+(run\s+)?test|yarn\s+test|pnpm\s+(run\s+)?test|npx\s+(jest|vitest)|pytest|python3?\s+-m\s+pytest|jest|vitest|go\s+test|cargo\s+test|mvn\s+(\S+\s+)*test|gradlew?\s+(\S+\s+)*test|rspec|phpunit|mix\s+test|swift\s+test)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommitCommand = new(@"\bgit\s+commit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A failure count above zero, or a runner's explicit failure banner.
    private static readonly Regex TestFailed = new(
        @"\b[1-9]\d*\s+(failed|failing|failures?|errors?)\b|Failed:\s+[1-9]|Failed!\s+-|\bFAILED\b|Test Run Failed|Build FAILED|npm ERR!|error\s+CS\d{4}|^FAIL\s",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TestPassed = new(
        @"\b(passed|passing)\b|Test Run Successful|\bPASS\b|\bok\b\s+\S+|Tests?:\s+\d+\s+passed|test result: ok",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsRead(ToolCall t) => ReadNames.Contains(t.Name);

    public static bool IsEdit(ToolCall t) =>
        EditNames.Contains(t.Name)
        || t.Name.Contains("replaceString", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("createFile", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("insertEdit", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("applyPatch", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("editFile", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("replace_string", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("create_file", StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains("insert_edit", StringComparison.OrdinalIgnoreCase);

    public static bool IsShell(ToolCall t) =>
        ShellNames.Contains(t.Name) || t.Name.Contains("terminal", StringComparison.OrdinalIgnoreCase);

    // Only the first line counts: later lines are often heredoc bodies or scripts that merely mention a command.
    public static bool IsTestRun(ToolCall t) => IsShell(t) && t.Target is { } cmd && TestCommand.IsMatch(FirstLine(cmd));

    public static bool IsCommit(ToolCall t) => IsShell(t) && t.Target is { } cmd && CommitCommand.IsMatch(FirstLine(cmd)) && !t.IsError;

    private static string FirstLine(string s) => s.Split('\n', 2)[0];

    /// <summary>True = passed, false = failed, null = can't tell from what was recorded.</summary>
    public static bool? TestPassedResult(ToolCall t)
    {
        if (t.IsError) return false;
        if (t.ResultTail is not { Length: > 0 } tail) return null;
        if (TestFailed.IsMatch(tail)) return false;
        if (TestPassed.IsMatch(tail)) return true;
        return null;
    }

    /// <summary>A short, stable label for a shell command, used to group noisy commands across sessions.</summary>
    public static string CommandLabel(string command)
    {
        var line = command.Split('\n')[0].Trim();
        // Strip leading "cd x &&" so the actual command shows.
        var amp = line.LastIndexOf("&&", StringComparison.Ordinal);
        if (amp >= 0) line = line[(amp + 2)..].Trim();
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        return string.Join(' ', words);
    }
}

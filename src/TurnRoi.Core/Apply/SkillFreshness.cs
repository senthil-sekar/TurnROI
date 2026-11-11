using System.Text.RegularExpressions;

namespace TurnRoi.Core.Apply;

public enum OutlineState { Fresh, Stale, Missing }

public sealed record OutlineStatus(string SkillFile, string FilePath, OutlineState State, string RecordedHash, string? CurrentHash);

/// <summary>
/// Checks and refreshes the file outlines in TurnROI-generated skills. Each outline records its file's content hash; an
/// outline is stale when the file has changed since, so its line numbers may point at the wrong place.
/// </summary>
public static class SkillFreshness
{
    private static readonly Regex Marker = new(
        @"<!-- turnroi:outline path=(?<path>\S+) sha=(?<sha>[0-9a-f]+) -->\r?\n(?<body>[\s\S]*?)<!-- turnroi:outline-end -->\r?\n?",
        RegexOptions.Compiled);

    private static readonly Regex CommitNote = new(@"Line numbers are from (commit `[0-9a-f]+`|the time this was generated)", RegexOptions.Compiled);

    public static IEnumerable<string> SkillFiles(string projectDir) =>
        new[] { ".claude/skills", ".cursor/skills", ".github/skills" }
            .Select(d => Path.Combine(projectDir, d, RepoGuidance.SkillName, "SKILL.md"))
            .Where(File.Exists);

    public static IReadOnlyList<OutlineStatus> Check(string projectDir, Func<string, string?>? read = null)
    {
        read ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        var result = new List<OutlineStatus>();
        foreach (var skill in SkillFiles(projectDir))
        {
            var text = read(skill) ?? "";
            foreach (Match m in Marker.Matches(text))
            {
                var rel = m.Groups["path"].Value;
                var recorded = m.Groups["sha"].Value;
                var content = read(Path.Combine(projectDir, rel));
                var current = content is null ? null : Outline.Hash(content);
                var state = current is null ? OutlineState.Missing : current == recorded ? OutlineState.Fresh : OutlineState.Stale;
                result.Add(new OutlineStatus(skill, rel, state, recorded, current));
            }
        }
        return result;
    }

    /// <summary>Plans rewrites of stale outlines (and removal of outlines for deleted files) in place.</summary>
    public static IReadOnlyList<FileChange> PlanRefresh(string projectDir, string? commit, Func<string, string?>? read = null)
    {
        read ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        var changes = new List<FileChange>();
        foreach (var skill in SkillFiles(projectDir))
        {
            var before = read(skill);
            if (before is null) continue;
            var changed = false;
            var after = Marker.Replace(before, m =>
            {
                var rel = m.Groups["path"].Value;
                var content = read(Path.Combine(projectDir, rel));
                if (content is null) { changed = true; return ""; }
                if (Outline.Hash(content) == m.Groups["sha"].Value) return m.Value;
                changed = true;
                return Outline.Of(rel, content) is { } o ? Outline.Section(o) : "";
            });
            if (!changed) continue;
            if (commit is not null) after = CommitNote.Replace(after, $"Line numbers are from commit `{commit}`");
            changes.Add(new FileChange(skill, before, after, "refresh stale outlines"));
        }
        return changes;
    }
}

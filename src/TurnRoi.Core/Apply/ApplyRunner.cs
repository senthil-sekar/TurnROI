using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurnRoi.Core.Apply;

/// <summary>
/// Writes planned changes after backing up every file it touches to <c>.turnroi/backup/&lt;timestamp&gt;/</c>, so
/// <see cref="Undo"/> can put the project back exactly as it was.
/// </summary>
public static class ApplyRunner
{
    public const string StateDir = ".turnroi";

    public static string Apply(string projectDir, IReadOnlyList<FileChange> changes, DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss-fff");
        var backupDir = Path.Combine(projectDir, StateDir, "backup", stamp);
        Directory.CreateDirectory(backupDir);
        // Backups are local state, never something to commit.
        File.WriteAllText(Path.Combine(projectDir, StateDir, ".gitignore"), "*\n");

        var manifest = new BackupManifest { Created = stamp };
        var n = 0;
        foreach (var change in changes)
        {
            var entry = new BackupEntry { Path = Path.GetRelativePath(projectDir, change.Path), Existed = change.Before is not null };
            if (change.Before is not null)
            {
                entry.Copy = $"{n++}.bak";
                File.WriteAllText(Path.Combine(backupDir, entry.Copy), change.Before);
            }
            manifest.Entries.Add(entry);
        }
        File.WriteAllText(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest));

        foreach (var change in changes)
        {
            if (change.After is null)
            {
                if (File.Exists(change.Path)) File.Delete(change.Path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
            File.WriteAllText(change.Path, change.After);
        }
        return backupDir;
    }

    /// <summary>Restores the most recent backup: changed files get their old content, created files are removed.</summary>
    /// <returns>The restored paths, or null when there is nothing to undo.</returns>
    public static IReadOnlyList<string>? Undo(string projectDir)
    {
        var root = Path.Combine(projectDir, StateDir, "backup");
        if (!Directory.Exists(root)) return null;
        var latest = Directory.GetDirectories(root).OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (latest is null) return null;

        var manifestPath = Path.Combine(latest, "manifest.json");
        var manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(manifestPath), BackupJsonContext.Default.BackupManifest)
            : null;
        if (manifest is null) return null;

        var restored = new List<string>();
        foreach (var entry in manifest.Entries)
        {
            var path = Path.Combine(projectDir, entry.Path);
            if (entry.Existed && entry.Copy is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, File.ReadAllText(Path.Combine(latest, entry.Copy)));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                RemoveEmptyParents(Path.GetDirectoryName(path), projectDir);
            }
            restored.Add(entry.Path);
        }
        Directory.Delete(latest, recursive: true);
        return restored;
    }

    private static void RemoveEmptyParents(string? dir, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt);
        while (dir is not null && Path.GetFullPath(dir) != stop && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}

internal sealed class BackupManifest
{
    public string Created { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = new();
}

internal sealed class BackupEntry
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? Copy { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

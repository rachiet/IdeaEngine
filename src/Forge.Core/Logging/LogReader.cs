namespace Forge.Core.Logging;

/// <summary>
/// Reads a project's log back, at either scope. Because the file is already
/// per-project, the whole file IS the project story; a task view is the same
/// lines with one filter. This is the read side of "trackable for any story".
/// </summary>
public static class LogReader
{
    /// <summary>
    /// Entries for the whole project (task = null) or one task, oldest first.
    /// A task query keeps only lines whose task column matches — and those lines
    /// still belong to the project, which is why the project query is a superset.
    /// </summary>
    public static IReadOnlyList<LogEntry> Read(string logPath, long? task = null)
    {
        if (!File.Exists(logPath)) return [];

        List<LogEntry> entries = [];
        foreach (var line in File.ReadLines(logPath))
        {
            if (line.Length == 0) continue;
            var entry = LogEntry.Deserialize(line);
            if (task is null || entry.Task == task) entries.Add(entry);
        }
        return entries;
    }
}

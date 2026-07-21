namespace Forge.Core.Tools;

/// <summary>
/// The one implementation of "is this path inside the task workspace?".
/// Both the process executor (command arguments) and the native file tools
/// (read_file/write_file/list_dir/grep) resolve through this type, so there is a
/// single place a jail escape could be got wrong rather than two that must agree.
/// </summary>
public sealed class PathJail
{
    public string Root { get; }

    public PathJail(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Jail root must be a non-empty path.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>True when an already-resolved absolute path is the jail root or below it.</summary>
    public bool Contains(string fullPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return normalized == Root ||
               normalized.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve a workspace-relative path to an absolute one, refusing anything that
    /// escapes. Absolute inputs are refused by construction: Path.Combine lets an
    /// absolute second argument win, and the result then fails Contains.
    /// </summary>
    public string Resolve(string path, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolJailViolationException("Empty path.");
        if (path.StartsWith('~'))
            throw new ToolJailViolationException($"Path '{path}' references the home directory.");

        var full = Path.GetFullPath(Path.Combine(baseDir ?? Root, path));
        if (!Contains(full))
            throw new ToolJailViolationException(
                $"Path '{path}' resolves outside the task workspace.");
        return full;
    }

    /// <summary>Workspace-relative rendering, for observations shown to the model.</summary>
    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);
}

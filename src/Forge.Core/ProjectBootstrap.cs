using Dapper;
using Forge.Core.Db;
using Forge.Core.Workspaces;

namespace Forge.Core;

/// <summary>Creates the on-disk layout for a client project under ForgeDataRoot.</summary>
public static class ProjectBootstrap
{
    public static void Init(ForgePaths paths, string name)
    {
        ForgePaths.ValidName(name);

        // A project exists in three places — the directory, the registry row, the
        // bare repo — and they can disagree if a previous init half-finished. Check
        // all three up front so a broken remnant reports "already exists" instead of
        // being silently completed, and so the registry INSERT never surprises us
        // with a primary-key violation partway through.
        using var global = Database.OpenGlobal(paths.GlobalDb);
        var registered = global.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM projects WHERE name = @name", new { name }) > 0;

        if (Directory.Exists(paths.ProjectDir(name)) || registered)
            throw new InvalidOperationException(
                $"Project '{name}' already exists at {paths.ProjectDir(name)}. " +
                "Delete that directory and its registry row to recreate it.");

        Directory.CreateDirectory(paths.ProjectDir(name));
        Directory.CreateDirectory(paths.WorkspacesDir(name));

        using (var project = Database.OpenProject(paths.ProjectDb(name))) { }

        InitBareRepo(paths.ProjectBareRepo(name), name);

        global.Execute("INSERT INTO projects (name) VALUES (@name)", new { name });
    }

    /// <summary>
    /// The bare repo gets a seed commit immediately. An empty repo has no HEAD, so
    /// cloning one and branching from it fails — every task workspace would have to
    /// special-case "is this the first task?". One commit at init removes the case.
    /// PROJECT.md is a stub here; the PM authors the real one in M2.
    /// </summary>
    private static void InitBareRepo(string repoPath, string project)
    {
        Git.Require(Path.GetDirectoryName(repoPath)!,
            "init", "--bare", "--initial-branch", WorkspaceManager.TrunkBranch, repoPath);

        var seed = Path.Combine(Path.GetTempPath(), $"forge-seed-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(seed);
            Git.Require(seed, "init", "--initial-branch", WorkspaceManager.TrunkBranch);
            File.WriteAllText(Path.Combine(seed, "PROJECT.md"),
                $"# {project}\n\nCreated by Forge. Requirements and design not yet authored.\n");
            Git.Require(seed, "add", "-A");
            Git.Require(seed, "commit", "-m", "chore: initialise project repository");
            Git.Require(seed, "push", repoPath, WorkspaceManager.TrunkBranch);
        }
        finally
        {
            if (Directory.Exists(seed)) Directory.Delete(seed, recursive: true);
        }
    }
}

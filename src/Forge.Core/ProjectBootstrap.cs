using System.Diagnostics;
using Dapper;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Creates the on-disk layout for a client project under ForgeDataRoot.</summary>
public static class ProjectBootstrap
{
    public static void Init(ForgePaths paths, string name)
    {
        ForgePaths.ValidName(name);
        if (Directory.Exists(paths.ProjectDir(name)))
            throw new InvalidOperationException($"Project '{name}' already exists at {paths.ProjectDir(name)}.");

        Directory.CreateDirectory(paths.ProjectDir(name));
        Directory.CreateDirectory(paths.WorkspacesDir(name));

        using (var project = Database.OpenProject(paths.ProjectDb(name))) { }

        InitBareRepo(paths.ProjectBareRepo(name));

        using var global = Database.OpenGlobal(paths.GlobalDb);
        global.Execute("INSERT INTO projects (name) VALUES (@name)", new { name });
    }

    private static void InitBareRepo(string repoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("--bare");
        psi.ArgumentList.Add(repoPath);

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git init --bare failed: {stderr.Trim()}");
    }
}

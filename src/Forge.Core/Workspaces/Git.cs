using System.Diagnostics;

namespace Forge.Core.Workspaces;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Output => string.IsNullOrWhiteSpace(Stdout) ? Stderr.Trim() : Stdout.Trim();
}

public sealed class GitException(string command, GitResult result)
    : InvalidOperationException($"git {command} failed ({result.ExitCode}): {result.Stderr.Trim()}");

/// <summary>
/// Harness-side git. Distinct from the agent's run() tool by design: this is
/// trusted mechanical code that reads and writes real repo state, which is why
/// merge and CI status come from here and never from an agent's claim
/// (spec Principle 1, §8).
/// </summary>
public static class Git
{
    /// <summary>
    /// Commits are attributed to the harness explicitly rather than inheriting the
    /// machine's git config, which may be absent on a server and is not ours anyway.
    /// </summary>
    private static readonly string[] Identity =
    [
        "-c", "user.name=Forge",
        "-c", "user.email=forge@localhost",
        "-c", "commit.gpgsign=false",
    ];

    public static GitResult Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in Identity) psi.ArgumentList.Add(arg);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git — is it installed and on PATH?");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitResult(process.ExitCode, stdout, stderr);
    }

    public static GitResult Require(string workingDir, params string[] args)
    {
        var result = Run(workingDir, args);
        return result.Ok ? result : throw new GitException(string.Join(' ', args), result);
    }
}

using System.Diagnostics;
using System.Text;

namespace Forge.Core.Ci;

/// <summary>The outcome of a CI step. Skipped counts as passed — there was nothing to fail.</summary>
public sealed record CiResult(bool Passed, string Step, string Output, bool Skipped = false)
{
    public static CiResult Skip(string why) => new(true, "detect", why, Skipped: true);

    /// <summary>A short line for the log; the full output goes to the engineer as feedback.</summary>
    public string Summary => Skipped
        ? $"skipped: {Output}"
        : $"{Step}: {(Passed ? "passed" : "FAILED")}";
}

/// <summary>
/// CI is harness-run, zero LLM tokens (spec §11): the harness compiles and tests
/// the task's code itself instead of believing the agent's "it builds". Runs
/// dotnet build, then dotnet test, in the task workspace. This is trusted
/// mechanical code — like Git.cs — so it runs dotnet directly, not through the
/// agent's jailed executor.
///
/// The Principal never reviews code that fails CI, so a failure short-circuits
/// straight back to the engineer with the compiler/test output as feedback.
/// </summary>
public static class CiRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public static CiResult Run(string workspaceDir)
    {
        // Nothing to build is not a failure — a docs-only task, or the solution
        // hasn't been scaffolded yet. Pass with a note rather than block.
        var buildable = Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workspaceDir, "*.csproj", SearchOption.AllDirectories))
            .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));
        if (!buildable)
            return CiResult.Skip("no .sln/.csproj — nothing to build (docs-only or not yet scaffolded)");

        var build = Dotnet(workspaceDir, "build", "--nologo");
        if (!build.Passed) return build;

        return Dotnet(workspaceDir, "test", "--nologo");
    }

    private static CiResult Dotnet(string dir, string step, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(step);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet — is the .NET SDK on PATH?");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            return new CiResult(false, step, $"dotnet {step} timed out after {Timeout.TotalMinutes:0} minutes.");
        }

        var output = new StringBuilder(stdout.GetAwaiter().GetResult());
        var err = stderr.GetAwaiter().GetResult();
        if (err.Length > 0) output.Append('\n').Append(err);
        return new CiResult(process.ExitCode == 0, step, output.ToString().Trim());
    }
}

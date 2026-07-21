using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Secrets;

namespace Forge.Core.Tools;

public sealed record ToolResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed class ToolJailViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Executes agent-requested commands under mechanical supervision (spec §11):
/// no shell, allowlisted binaries only, working directory jailed to the task
/// workspace, per-command timeout, {{secret:NAME}} substituted at exec time.
/// Secret values are redacted from captured output so they never reach the
/// model, the DB, or logs.
/// </summary>
public sealed partial class ToolExecutor(
    string jailRoot,
    IReadOnlyCollection<string> allowedBinaries,
    SecretsVault vault,
    TimeSpan? defaultTimeout = null,
    IReadOnlyDictionary<string, string>? environment = null)
{
    private readonly PathJail _jail = new(jailRoot);
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>Extra variables to hand child processes, on top of the allowlist.</summary>
    private readonly IReadOnlyDictionary<string, string> _environment =
        environment ?? new Dictionary<string, string>();

    public PathJail Jail => _jail;

    [GeneratedRegex(@"\{\{secret:([A-Za-z0-9_]+)\}\}")]
    private static partial Regex SecretRef();

    public async Task<ToolResult> RunAsync(
        string command,
        string? workingSubdir = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var argv = Tokenize(command);
        if (argv.Count == 0)
            throw new ArgumentException("Empty command.", nameof(command));

        var binary = argv[0];
        if (binary.Contains('/') || binary.Contains('\\') ||
            !allowedBinaries.Contains(binary, StringComparer.Ordinal))
        {
            throw new ToolJailViolationException(
                $"Binary '{binary}' is not on the allowlist ({string.Join(", ", allowedBinaries)}).");
        }

        var workingDir = ResolveWorkingDir(workingSubdir);
        foreach (var arg in argv.Skip(1))
            RejectJailEscape(arg, workingDir);

        var secretsUsed = new Dictionary<string, string>();
        var finalArgv = argv.Select(a => SubstituteSecrets(a, secretsUsed)).ToList();

        var psi = new ProcessStartInfo
        {
            FileName = finalArgv[0],
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in finalArgv.Skip(1)) psi.ArgumentList.Add(arg);
        ScrubEnvironment(psi);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timedOut = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? _defaultTimeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var stdout = Redact(await stdoutTask.ConfigureAwait(false), secretsUsed);
        var stderr = Redact(await stderrTask.ConfigureAwait(false), secretsUsed);
        return new ToolResult(timedOut ? -1 : process.ExitCode, stdout, stderr, timedOut);
    }

    /// <summary>
    /// Child processes would otherwise inherit Forge's whole environment — which
    /// holds the harness's own provider keys. An agent's `dotnet run` on code it
    /// just wrote is arbitrary code execution, so inheritance is a credential leak.
    /// Build the environment from an allowlist instead: a key added to forge_env
    /// tomorrow is invisible to agents by default, with nothing to remember.
    ///
    /// HOME points at the workspace so a child cannot read ~/forge_env either.
    /// This raises the bar; it is not a sandbox. Real isolation is a container,
    /// and that is a later problem than M1.
    /// </summary>
    private void ScrubEnvironment(ProcessStartInfo psi)
    {
        var inherited = psi.Environment;
        var passThrough = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThroughNames)
            if (inherited.TryGetValue(name, out var value) && value is not null)
                passThrough[name] = value;

        // Toolchain caches are shared on purpose: re-downloading NuGet packages
        // for every task would make the jail expensive rather than safe.
        foreach (var (name, value) in inherited)
            if (value is not null && (name.StartsWith("DOTNET_", StringComparison.Ordinal) ||
                                      name.StartsWith("NUGET_", StringComparison.Ordinal)))
                passThrough[name] = value;

        psi.Environment.Clear();
        foreach (var (name, value) in passThrough) psi.Environment[name] = value;
        foreach (var (name, value) in _environment) psi.Environment[name] = value;
        psi.Environment["HOME"] = _jail.Root;
        psi.Environment["USERPROFILE"] = _jail.Root;
    }

    private static readonly string[] PassThroughNames =
        ["PATH", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "TERM", "SystemRoot", "ComSpec"];

    private string ResolveWorkingDir(string? subdir)
    {
        var dir = subdir is null ? _jail.Root : _jail.Resolve(subdir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Working directory '{dir}' does not exist.");
        return dir;
    }

    /// <summary>
    /// Heuristic path guard on arguments: anything that syntactically points
    /// outside the jail (absolute paths, ~, or ..-escapes) is refused. Flags in
    /// --name=value form are checked on their value part.
    /// </summary>
    private void RejectJailEscape(string arg, string workingDir)
    {
        var candidate = arg;
        var eq = arg.IndexOf('=');
        if (arg.StartsWith('-') && eq >= 0) candidate = arg[(eq + 1)..];

        if (candidate.StartsWith('~'))
            throw new ToolJailViolationException($"Argument '{arg}' references the home directory.");

        var looksAbsolute = Path.IsPathRooted(candidate);
        var hasDotDot = candidate.Split('/', '\\').Contains("..");
        if (!looksAbsolute && !hasDotDot) return;

        var resolved = Path.GetFullPath(looksAbsolute ? candidate : Path.Combine(workingDir, candidate));
        if (!_jail.Contains(resolved))
            throw new ToolJailViolationException(
                $"Argument '{arg}' resolves outside the task workspace ({resolved}).");
    }

    private string SubstituteSecrets(string arg, Dictionary<string, string> secretsUsed) =>
        SecretRef().Replace(arg, m =>
        {
            var name = m.Groups[1].Value;
            var value = vault.Get(name);
            secretsUsed[name] = value;
            return value;
        });

    private static string Redact(string output, Dictionary<string, string> secretsUsed)
    {
        foreach (var (name, value) in secretsUsed)
            output = output.Replace(value, $"{{{{secret:{name}}}}}");
        return output;
    }

    /// <summary>Quote-aware tokenizer. No shell is involved, so shell operators are refused loudly
    /// rather than silently passed to the binary as literal arguments.</summary>
    internal static List<string> Tokenize(string command)
    {
        List<string> tokens = [];
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (inSingle)
            {
                if (c == '\'') inSingle = false; else current.Append(c);
            }
            else if (inDouble)
            {
                if (c == '"') inDouble = false;
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                    current.Append(command[++i]);
                else current.Append(c);
            }
            else if (c == '\'') { inSingle = true; hasToken = true; }
            else if (c == '"') { inDouble = true; hasToken = true; }
            else if (c == '\\' && i + 1 < command.Length) { current.Append(command[++i]); hasToken = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else if (c is '|' or '&' or ';' or '<' or '>' or '`' or '$' or '(' or ')')
            {
                throw new ArgumentException(
                    $"Shell operator '{c}' is not supported: commands run without a shell. " +
                    "Run one binary per command.", nameof(command));
            }
            else { current.Append(c); hasToken = true; }
        }

        if (inSingle || inDouble)
            throw new ArgumentException("Unbalanced quote in command.", nameof(command));
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }
}

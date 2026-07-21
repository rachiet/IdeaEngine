using Forge.Core.Secrets;
using Forge.Core.Tools;

namespace Forge.Tests;

public class ToolExecutorTests : IDisposable
{
    private static readonly string[] Allowed = ["echo", "cat", "sleep", "git"];

    private readonly string _root;
    private readonly string _jail;
    private readonly SecretsVault _vault;
    private readonly ToolExecutor _executor;

    public ToolExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}");
        _jail = Path.Combine(_root, "workspaces", "task-1");
        Directory.CreateDirectory(_jail);
        _vault = new SecretsVault(Path.Combine(_root, "vault"));
        _executor = new ToolExecutor(_jail, Allowed, _vault);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Runs_allowlisted_binary_and_captures_output()
    {
        var result = await _executor.RunAsync("echo hello world");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello world", result.Stdout.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Disallowed_binaries_and_paths_to_binaries_are_refused()
    {
        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("python evil.py"));
        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("/bin/echo hi"));
    }

    [Fact]
    public async Task Working_dir_cannot_escape_the_jail()
    {
        await Assert.ThrowsAsync<ToolJailViolationException>(
            () => _executor.RunAsync("echo hi", workingSubdir: "../task-2"));
        await Assert.ThrowsAsync<ToolJailViolationException>(
            () => _executor.RunAsync("echo hi", workingSubdir: "/tmp"));
    }

    [Fact]
    public async Task Path_arguments_cannot_escape_the_jail()
    {
        await File.WriteAllTextAsync(Path.Combine(_jail, "ok.txt"), "inside");

        var inside = await _executor.RunAsync("cat ok.txt");
        Assert.Equal("inside", inside.Stdout);

        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("cat /etc/passwd"));
        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("cat ../../forge.db"));
        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("cat ~/secrets"));
        await Assert.ThrowsAsync<ToolJailViolationException>(() => _executor.RunAsync("git --work-tree=/ status"));
    }

    [Fact]
    public async Task Shell_operators_are_refused_loudly()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _executor.RunAsync("echo hi | cat"));
        await Assert.ThrowsAsync<ArgumentException>(() => _executor.RunAsync("echo hi > out.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => _executor.RunAsync("echo $(cat /etc/passwd)"));
    }

    [Fact]
    public async Task Secrets_are_substituted_at_exec_time_and_redacted_from_output()
    {
        // Substitution proof: the file name only resolves if {{secret:...}} became the real value.
        _vault.Set("TARGET_FILE", "payload.txt");
        await File.WriteAllTextAsync(Path.Combine(_jail, "payload.txt"), "contents");
        var read = await _executor.RunAsync("cat {{secret:TARGET_FILE}}");
        Assert.Equal("contents", read.Stdout);

        // Redaction proof: a value echoed back never appears in captured output.
        _vault.Set("API_KEY", "sk-live-abc123");
        var echoed = await _executor.RunAsync("echo prefix-{{secret:API_KEY}}-suffix");
        Assert.DoesNotContain("sk-live-abc123", echoed.Stdout);
        Assert.Equal("prefix-{{secret:API_KEY}}-suffix", echoed.Stdout.Trim());
    }

    [Fact]
    public async Task Unknown_secret_names_fail_before_exec()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _executor.RunAsync("echo {{secret:NOPE}}"));
    }

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        var result = await _executor.RunAsync("sleep 30", timeout: TimeSpan.FromMilliseconds(300));
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void Tokenizer_honors_quotes()
    {
        Assert.Equal(["git", "commit", "-m", "a message", "--author=Jo Doe"],
            ToolExecutor.Tokenize("""git commit -m 'a message' --author="Jo Doe" """));
        Assert.Throws<ArgumentException>(() => ToolExecutor.Tokenize("echo 'unterminated"));
    }
}

public class SecretsVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"forge-vault-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_values_encrypted_at_rest()
    {
        var vault = new SecretsVault(_dir);
        vault.Set("STRIPE_API_KEY", "sk-live-xyz");
        Assert.Equal("sk-live-xyz", vault.Get("STRIPE_API_KEY"));
        Assert.True(vault.Contains("STRIPE_API_KEY"));
        Assert.Equal(["STRIPE_API_KEY"], vault.Names());

        // Value must not sit in plaintext on disk.
        Assert.DoesNotContain("sk-live-xyz", File.ReadAllText(Path.Combine(_dir, "secrets.json")));

        // A fresh instance reads the same key file.
        Assert.Equal("sk-live-xyz", new SecretsVault(_dir).Get("STRIPE_API_KEY"));
    }

    [Fact]
    public void Key_file_is_owner_only()
    {
        new SecretsVault(_dir).Set("A", "b");
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "key.bin"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Missing_and_invalid_names_throw()
    {
        var vault = new SecretsVault(_dir);
        Assert.Throws<KeyNotFoundException>(() => vault.Get("MISSING"));
        Assert.Throws<ArgumentException>(() => vault.Set("bad name", "v"));
        Assert.Throws<ArgumentException>(() => vault.Set("bad-name", "v"));
        Assert.Throws<ArgumentException>(() => vault.Set("", "v"));
    }
}

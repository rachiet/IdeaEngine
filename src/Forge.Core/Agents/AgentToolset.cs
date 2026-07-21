using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Db;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>What executing one tool produced, and whether it ends the loop.</summary>
public sealed record ToolOutcome(string Observation, EndReason? End = null);

/// <summary>
/// The v1 tool surface (spec §4.1), bound to one agent's workspace. Which tools
/// exist is the recipe's business, not this class's — so a role gains or loses a
/// capability by editing data. Every path goes through the PathJail and the
/// role's PathScope, and every command through the ToolExecutor, so a model that
/// asks for something out of bounds gets a refusal as an observation rather than
/// an effect.
/// </summary>
public sealed class AgentToolset(
    ToolExecutor executor,
    IDbConnection connection,
    AgentRecipe recipe,
    TaskRecord? task = null)
{
    private const int MaxObservationChars = 8_000;
    private const int DefaultReadLines = 400;

    private readonly PathJail _jail = executor.Jail;
    private readonly TaskRepository _tasks = new(connection);
    private readonly MessageRepository _messages = new(connection);
    private readonly MilestoneRepository _milestones = new(connection);

    /// <summary>Last note the agent wrote, for the harness's end-of-run fallback.</summary>
    public string? LastProgressNote { get; private set; }

    /// <summary>What the agent last said to the client, when it is a chat role.</summary>
    public string? LastReply { get; private set; }

    /// <summary>One line per tool, rendered into the prompt so docs cannot drift from code.</summary>
    public static readonly IReadOnlyDictionary<string, string> Catalogue =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_file"] = "read_file(path, [start], [end]) — read a file, optionally a line range.",
            ["list_dir"] = "list_dir([path]) — list a directory (defaults to the workspace root).",
            ["grep"] = "grep(pattern, [path]) — regex search across files.",
            ["write_file"] = "write_file(path, content) — create or overwrite a file, whole contents.",
            ["run"] = "run(command, [cwd]) — run one binary.",
            ["add_milestone"] = "add_milestone(name, [description], [ordinal]) — add a milestone to the plan.",
            ["reply"] = "reply(message) — say this to the client and end your turn.",
            ["progress_note"] = "progress_note(note) — save state for your successor.",
            ["done"] = "done(summary) — you believe the work is complete.",
            ["escalate"] = "escalate(reason) — you are blocked and need a human decision.",
        };

    public async Task<ToolOutcome> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        if (!recipe.Tools.Contains(call.Name, StringComparer.Ordinal))
        {
            return new ToolOutcome(
                $"ERROR: no tool '{call.Name}' is available to you. " +
                $"Available: {string.Join(", ", recipe.Tools)}.");
        }

        try
        {
            return call.Name switch
            {
                "read_file" => ReadFile(call),
                "list_dir" => ListDir(call),
                "grep" => Grep(call),
                "write_file" => WriteFile(call),
                "run" => await RunAsync(call, ct).ConfigureAwait(false),
                "add_milestone" => AddMilestone(call),
                "reply" => Reply(call),
                "progress_note" => ProgressNote(call),
                "done" => Done(call),
                "escalate" => Escalate(call),
                _ => new ToolOutcome($"ERROR: tool '{call.Name}' is not implemented."),
            };
        }
        // Refusals are observations, not crashes: the agent should see the boundary
        // and correct, exactly as it would see a compiler error.
        catch (ToolJailViolationException ex) { return new ToolOutcome($"REFUSED: {ex.Message}"); }
        catch (ToolCallException ex) { return new ToolOutcome($"ERROR: {ex.Message}"); }
        catch (ArgumentException ex) { return new ToolOutcome($"ERROR: {ex.Message}"); }
        catch (IOException ex) { return new ToolOutcome($"ERROR: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return new ToolOutcome($"ERROR: {ex.Message}"); }
        catch (KeyNotFoundException ex) { return new ToolOutcome($"ERROR: {ex.Message}"); }
    }

    /// <summary>Jail first (can it be reached?), then role scope (may this role reach it?).</summary>
    private string Resolve(string relativePath)
    {
        var full = _jail.Resolve(relativePath);
        var relative = _jail.Relative(full);
        if (!recipe.Scope.Allows(relative))
            throw new ToolJailViolationException(
                $"'{relative}' is outside your role's scope ({recipe.Scope.Describe()}).");
        return full;
    }

    private ToolOutcome ReadFile(ToolCall call)
    {
        var path = Resolve(call.Arg("path"));
        if (!File.Exists(path)) return new ToolOutcome($"ERROR: no such file '{call.Arg("path")}'.");

        var lines = File.ReadAllLines(path);
        var start = Math.Max(1, call.OptionalInt("start") ?? 1);
        var end = Math.Min(lines.Length, call.OptionalInt("end") ?? start + DefaultReadLines - 1);

        var sb = new StringBuilder($"{_jail.Relative(path)} (lines {start}-{end} of {lines.Length}):\n");
        for (var i = start; i <= end; i++) sb.Append(i).Append('\t').AppendLine(lines[i - 1]);
        if (end < lines.Length) sb.Append($"... {lines.Length - end} more lines; read again with start={end + 1}.");
        return new ToolOutcome(Truncate(sb.ToString()));
    }

    private ToolOutcome ListDir(ToolCall call)
    {
        var dir = Resolve(call.Optional("path") ?? ".");
        if (!Directory.Exists(dir)) return new ToolOutcome($"ERROR: no such directory '{call.Optional("path") ?? "."}'.");

        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Where(e => Path.GetFileName(e) != ".git")
            .Where(e => recipe.Scope.Allows(_jail.Relative(e) + (Directory.Exists(e) ? "/" : "")))
            .OrderBy(e => e, StringComparer.Ordinal)
            .Select(e => Directory.Exists(e) ? $"{_jail.Relative(e)}/" : _jail.Relative(e));
        return new ToolOutcome(Truncate(string.Join('\n', entries) is { Length: > 0 } s ? s : "(nothing in scope here)"));
    }

    private ToolOutcome Grep(ToolCall call)
    {
        var pattern = call.Arg("pattern");
        var root = Resolve(call.Optional("path") ?? ".");
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return new ToolOutcome($"ERROR: bad regex '{pattern}': {ex.Message}"); }

        var files = File.Exists(root)
            ? [root]
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .Where(f => recipe.Scope.Allows(_jail.Relative(f)));

        var hits = new StringBuilder();
        var count = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length && count < 200; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                hits.AppendLine($"{_jail.Relative(file)}:{i + 1}: {lines[i].Trim()}");
                count++;
            }
        }
        return new ToolOutcome(count == 0 ? $"No matches for '{pattern}'." : Truncate(hits.ToString()));
    }

    private ToolOutcome WriteFile(ToolCall call)
    {
        var path = Resolve(call.Arg("path"));
        var content = call.Args.TryGetValue("content", out var c) ? c : "";
        // The tag protocol eats the layout newline before </arg>; restore the
        // conventional trailing newline so files are not written without one.
        if (content.Length > 0 && !content.EndsWith('\n')) content += "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        File.WriteAllText(path, content);
        var lineCount = content.Count(ch => ch == '\n');
        return new ToolOutcome($"{(existed ? "Overwrote" : "Wrote")} {_jail.Relative(path)} ({lineCount} lines).");
    }

    private async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
    {
        var command = call.Arg("command");
        var result = await executor.RunAsync(command, call.Optional("cwd"), ct: ct).ConfigureAwait(false);

        var sb = new StringBuilder($"$ {command}\nexit code: {result.ExitCode}");
        if (result.TimedOut) sb.Append(" (TIMED OUT — process killed)");
        if (result.Stdout.Length > 0) sb.Append("\n--- stdout ---\n").Append(result.Stdout.TrimEnd());
        if (result.Stderr.Length > 0) sb.Append("\n--- stderr ---\n").Append(result.Stderr.TrimEnd());
        return new ToolOutcome(Truncate(sb.ToString()));
    }

    /// <summary>The milestone plan is a real table, not prose in a markdown file the harness can't query.</summary>
    private ToolOutcome AddMilestone(ToolCall call)
    {
        var name = call.Arg("name");
        var ordinal = call.OptionalInt("ordinal") ?? _milestones.NextOrdinal();
        var milestone = _milestones.Insert(new MilestoneRecord
        {
            Name = name,
            Description = call.Optional("description"),
            Ordinal = ordinal,
        });
        return new ToolOutcome($"Milestone {milestone.Id} recorded: #{ordinal} {name}.");
    }

    /// <summary>
    /// The client-facing turn. Recorded as a message so the conversation survives
    /// the instance that produced it — chat history lives in the DB, never in a
    /// transcript held in memory (Principle 2).
    /// </summary>
    private ToolOutcome Reply(ToolCall call)
    {
        var text = call.Arg("message");
        _messages.Insert(Message.Create(
            MessageType.Answer, SnakeCaseEnum.ToSnakeCase(recipe.Role), "client", text, task?.Id));
        LastReply = text;
        return new ToolOutcome("Delivered to the client.", EndReason.Done);
    }

    /// <summary>
    /// Persisted immediately, not at end of run: the note is the only thing a
    /// fresh instance inherits after a kill, so it must survive one.
    /// </summary>
    private ToolOutcome ProgressNote(ToolCall call)
    {
        var note = call.Arg("note");
        if (task is null) return new ToolOutcome("ERROR: progress_note needs a task; this run has none.");
        _tasks.SetProgressNote(task.Id, note);
        LastProgressNote = note;
        return new ToolOutcome("Progress note saved.");
    }

    private ToolOutcome Done(ToolCall call)
    {
        var summary = call.Arg("summary");
        if (task is not null) _tasks.SetProgressNote(task.Id, summary);
        LastProgressNote = summary;
        return new ToolOutcome("Work reported complete; the harness will verify and merge.", EndReason.Done);
    }

    private ToolOutcome Escalate(ToolCall call)
    {
        var reason = call.Arg("reason");
        var from = SnakeCaseEnum.ToSnakeCase(recipe.Role);
        // The PM is the escalation target for everyone else; the PM escalates to the client.
        var to = recipe.Role == AgentRole.Pm ? "client" : "pm";
        _messages.Insert(Message.Create(MessageType.Escalation, from, to, reason, task?.Id));
        if (task is not null) _tasks.SetProgressNote(task.Id, $"Escalated: {reason}");
        LastProgressNote = $"Escalated: {reason}";
        return new ToolOutcome($"Escalation sent to the {to}; stopping here.", EndReason.Escalated);
    }

    /// <summary>Tool output is untrusted and unbounded; the context window is not.</summary>
    private static string Truncate(string text) =>
        text.Length <= MaxObservationChars
            ? text
            : text[..MaxObservationChars] + $"\n... [truncated {text.Length - MaxObservationChars} chars]";
}

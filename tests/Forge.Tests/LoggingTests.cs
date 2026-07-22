using Forge.Core.Logging;

namespace Forge.Tests;

/// <summary>Captures entries in memory so a test can assert on the stream without a file.</summary>
public sealed class MemoryLogSink : ILogSink
{
    public List<LogEntry> Entries { get; } = [];
    public void Write(LogEntry entry) => Entries.Add(entry);
    public void Dispose() { }

    public IEnumerable<string> Types => Entries.Select(e => e.Type.Wire());
    public IEnumerable<LogEntry> ForTask(long task) => Entries.Where(e => e.Task == task);
}

public class EventTypeTests
{
    [Fact]
    public void Every_event_type_has_a_wire_form_and_round_trips()
    {
        foreach (var type in Enum.GetValues<EventType>())
        {
            var wire = type.Wire();
            // Typed events are domain.action; the free-form `message` channel is a
            // single token on purpose (a log line, not a mechanical event).
            Assert.Matches("^[a-z]+(\\.[a-z_]+)?$", wire);
            Assert.Equal(type, EventTypes.Parse(wire));      // round-trips
        }
    }

    [Fact]
    public void Tool_names_map_to_their_events()
    {
        Assert.Equal(EventType.ToolWriteFile, EventTypes.ForTool("write_file"));
        Assert.Equal(EventType.ToolListDir, EventTypes.ForTool("list_dir"));
        Assert.Null(EventTypes.ForTool("not_a_tool"));
    }

    [Fact]
    public void Domain_and_action_split_from_the_type_and_reassemble()
    {
        Assert.Equal(("tool", "write_file"), (EventType.ToolWriteFile.Domain(), EventType.ToolWriteFile.Action()));
        Assert.Equal(("git", "merge"), (EventType.GitMerge.Domain(), EventType.GitMerge.Action()));

        // message is single-token: a domain with no action.
        Assert.Equal("message", EventType.Message.Domain());
        Assert.Equal("", EventType.Message.Action());

        // Round-trip from the two stored columns back to the enum.
        Assert.Equal(EventType.GitMerge, EventTypes.FromColumns("git", "merge"));
        Assert.Equal(EventType.Message, EventTypes.FromColumns("message", ""));
    }

    [Fact]
    public void An_unknown_wire_string_is_rejected_rather_than_guessed()
    {
        Assert.Throws<FormatException>(() => EventTypes.Parse("tool.writefile"));
    }
}

public class LogEntryTests
{
    [Fact]
    public void Serialises_and_deserialises_including_a_message_with_separators()
    {
        var entry = LogEntry.Task_("todo", 7, EventType.ToolRun,
            "$ dotnet test | exit 0 : all green");  // contains | and : on purpose

        var round = LogEntry.Deserialize(entry.Serialize());

        Assert.Equal("todo", round.Project);
        Assert.Equal(7, round.Task);
        Assert.Equal(EventType.ToolRun, round.Type);
        Assert.Equal("$ dotnet test | exit 0 : all green", round.Message);
    }

    [Fact]
    public void Newlines_in_a_message_are_folded_to_keep_one_entry_per_line()
    {
        var entry = LogEntry.Task_("todo", 1, EventType.ToolReadFile, "line one\nline two\nline three");
        Assert.DoesNotContain('\n', entry.Serialize());
        Assert.Equal("line one line two line three", LogEntry.Deserialize(entry.Serialize()).Message);
    }

    [Fact]
    public void A_project_level_entry_has_no_task()
    {
        var entry = LogEntry.Project_("todo", EventType.Message, "client → pm: hi");
        Assert.Null(entry.Task);
        Assert.Null(LogEntry.Deserialize(entry.Serialize()).Task);
    }
}

public class FileSinkAndReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"forge-log-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_dir, "forge.log");

    public FileSinkAndReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_reader_returns_the_whole_project_or_one_task_from_the_same_file()
    {
        using (var sink = new FileLogSink(LogPath))
        {
            var log = new ForgeLogger(sink, "todo");
            log.Message("client → pm: build a todo app");        // project-level
            log.For(7).Event(EventType.ToolWriteFile, "created Update.cs");
            log.For(7).Event(EventType.GitMerge, "task/7 → master");
            log.For(9).Event(EventType.ToolWriteFile, "created Delete.cs");  // a different task
        }

        // Whole project: every line, tasks included, in order.
        var all = LogReader.Read(LogPath);
        Assert.Equal(4, all.Count);
        Assert.Equal(
            ["message", "tool.write_file", "git.merge", "tool.write_file"],
            all.Select(e => e.Type.Wire()));

        // One task: only that task's lines — but they were part of the project view above.
        var task7 = LogReader.Read(LogPath, task: 7);
        Assert.Equal(2, task7.Count);
        Assert.All(task7, e => Assert.Equal(7, e.Task));
        Assert.DoesNotContain(task7, e => e.Message.Contains("Delete.cs"));
    }

    [Fact]
    public void A_missing_log_file_reads_as_empty_rather_than_throwing()
    {
        Assert.Empty(LogReader.Read(Path.Combine(_dir, "nope.log")));
    }

    [Fact]
    public void A_composite_sink_fans_out_and_one_broken_sink_does_not_stop_the_others()
    {
        var memory = new MemoryLogSink();
        using var file = new FileLogSink(LogPath);
        using var composite = new CompositeLogSink(new ThrowingSink(), memory, file);

        composite.Write(LogEntry.Project_("todo", EventType.Message, "still delivered"));

        Assert.Single(memory.Entries);                       // reached the memory sink
        Assert.Single(LogReader.Read(LogPath));              // and the file sink
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogEntry entry) => throw new IOException("sink is down");
        public void Dispose() { }
    }
}

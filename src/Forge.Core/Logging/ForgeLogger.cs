namespace Forge.Core.Logging;

/// <summary>
/// What the harness calls to record events. Holds the sink, the project it is
/// logging for, and (optionally) the task it is bound to — so call sites emit a
/// one-line message and the columns are filled in for them.
///
/// Bind a task with <see cref="For"/> once at the top of a run; every event
/// after that carries the correlation without the call site repeating the id.
/// The typed helpers (Tool/Git/Lifecycle/…) stamp the eventType, so a caller
/// cannot pair the wrong domain with a message.
/// </summary>
public sealed class ForgeLogger
{
    private readonly ILogSink _sink;
    private readonly string _project;
    private readonly long? _task;

    public ForgeLogger(ILogSink sink, string project, long? task = null)
    {
        _sink = sink;
        _project = project;
        _task = task;
    }

    /// <summary>A logger bound to one task — its events carry that correlation.</summary>
    public ForgeLogger For(long task) => new(_sink, _project, task);

    /// <summary>Never writes anywhere. The default when nothing is wired.</summary>
    public static ForgeLogger Null { get; } = new(NullLogSink.Instance, "");

    /// <summary>
    /// Record one typed mechanical event. The EventType is the single source of
    /// truth for the domain and action columns — there is no separate category
    /// argument that could contradict it, so a git merge can never be mis-tagged
    /// as a lifecycle event. Tool events derive their type from the tool name
    /// (EventTypes.ForTool) and are never hand-written.
    /// </summary>
    public void Event(EventType type, string message) =>
        _sink.Write(new LogEntry(DateTimeOffset.UtcNow, _project, _task, type, message));

    /// <summary>
    /// The free-form channel — the line you actually read. Use it for agent↔client
    /// communication AND for ordinary service/debug logging from harness code
    /// ("creating util file X", "no ready work for the engineer"). All land in the
    /// <c>message</c> domain; the text says which it is.
    /// </summary>
    public void Message(string message) => Event(EventType.Message, message);
}

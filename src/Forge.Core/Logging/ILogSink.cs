namespace Forge.Core.Logging;

/// <summary>
/// Where log entries go. The one seam that makes the destination swappable: the
/// harness only ever calls <see cref="Write"/>, so pointing logs at a file, the
/// console, or a remote service is a matter of which sink is constructed — no
/// call site changes. The default is a file; anything else is a drop-in.
/// </summary>
public interface ILogSink : IDisposable
{
    void Write(LogEntry entry);
}

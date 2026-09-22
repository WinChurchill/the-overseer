namespace TheOverseer.Core.Diagnostics;

public enum MonitorLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Safe structural logging. Implementations must never receive or write titles,
/// paths, prompts, assistant text, questions, answers, command lines or
/// credentials; callers only pass stable codes, numbers and counters.
/// </summary>
public interface IMonitorLog
{
    void Write(MonitorLogLevel level, string code, string detail = "");

    bool IsDegraded { get; }
}

public sealed class NullMonitorLog : IMonitorLog
{
    public static NullMonitorLog Instance { get; } = new();

    private NullMonitorLog()
    {
    }

    public bool IsDegraded => false;

    public void Write(MonitorLogLevel level, string code, string detail = "")
    {
    }
}

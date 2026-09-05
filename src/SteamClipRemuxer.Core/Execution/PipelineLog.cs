namespace SteamClipRemuxer.Core.Execution;

public enum LogLevel { Info, Success, Warning, Error }

/// <summary>
/// Where the pipeline reports what it did. Deliberately not tied to any UI type, so Core
/// stays buildable without a desktop framework.
/// </summary>
public interface IPipelineLog
{
    void Write(LogLevel level, string message);
}

public sealed class NullPipelineLog : IPipelineLog
{
    public static readonly NullPipelineLog Instance = new();
    public void Write(LogLevel level, string message) { }
}

public sealed class DelegatePipelineLog : IPipelineLog
{
    private readonly Action<LogLevel, string> _write;
    public DelegatePipelineLog(Action<LogLevel, string> write) => _write = write;
    public void Write(LogLevel level, string message) => _write(level, message);
}

public static class PipelineLogExtensions
{
    public static void Info(this IPipelineLog log, string m) => log.Write(LogLevel.Info, m);
    public static void Success(this IPipelineLog log, string m) => log.Write(LogLevel.Success, m);
    public static void Warning(this IPipelineLog log, string m) => log.Write(LogLevel.Warning, m);
    public static void Error(this IPipelineLog log, string m) => log.Write(LogLevel.Error, m);
}

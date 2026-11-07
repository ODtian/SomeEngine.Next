namespace SomeEngine.Tools;

internal enum CommandKind
{
    Default = 0,
    Cpu = 1,
    Gpu = 2,
}

internal sealed record QueuedCommand(string Id, string Executable, string[] Arguments, string Directory,
    string OutputDirectory, Dictionary<string, string> Environment, string SessionId, CommandKind Kind,
    DateTimeOffset SubmittedAtUtc)
{
    internal const int DefaultTimeoutSeconds = 60;
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    public QueueSettings Settings { get; init; } = new();
    public string? DispatchReason { get; init; }
    public string? RemoteError { get; init; }
}

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
    public bool AllCores { get; init; }
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
}

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
    public SampleLimit? SampleLimit { get; init; }
    public string? DiagnosticScope { get; init; }

    public Dictionary<string, string> GetThreadEnvironment()
    {
        var values = Environment.Where(variable =>
            variable.Key.StartsWith("DOTNET_") || variable.Key.StartsWith("COMPlus_")
            || variable.Key.StartsWith("OMP_") || variable.Key.StartsWith("OPENBLAS_")
            || variable.Key.StartsWith("MKL_"))
            .ToDictionary(variable => variable.Key, variable => variable.Value);
        values["DOTNET_PROCESSOR_COUNT"] = Settings.CpuCoreCount.ToString();
        values["OMP_NUM_THREADS"] = Settings.CpuCoreCount.ToString();
        values["OPENBLAS_NUM_THREADS"] = Settings.CpuCoreCount.ToString();
        values["MKL_NUM_THREADS"] = Settings.CpuCoreCount.ToString();
        return values;
    }
}

using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace SomeEngine.Tools;

internal sealed class CommandQueue
{
    private readonly BlockingCollection<QueueChange> changes = new();
    private readonly Dictionary<string, SessionCommands> sessions = new();
    private readonly Queue<SessionCommands> turns = new();
    private readonly Dictionary<string, CommandCpu> cpuCommands = new();
    private readonly CpuUsage cpuUsage = new();
    private readonly CpuCores cpuCores = new();
    private readonly ConcurrencyLimiter commandConcurrency = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = Environment.ProcessorCount,
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });
    private Timer? cpuSampling;
    private bool gpuInUse;
    private bool allCoresInUse;

    public int TotalCoreCount => cpuCores.Count;

    public void Enqueue(QueuedCommand command) => changes.Add(new SubmittedCommand(command));

    public void Run()
    {
        foreach (QueueChange change in changes.GetConsumingEnumerable())
        {
            switch (change)
            {
                case SubmittedCommand submitted:
                    if (!sessions.TryGetValue(submitted.Command.SessionId, out SessionCommands? session))
                    {
                        session = new SessionCommands();
                        sessions.Add(submitted.Command.SessionId, session);
                        turns.Enqueue(session);
                    }
                    session.Commands.Enqueue(submitted.Command);
                    break;
                case CompletedCommand completed:
                    if (completed.Command.Kind == CommandKind.Gpu) gpuInUse = false;
                    if (completed.Command.AllCores) allCoresInUse = false;
                    cpuCommands.Remove(completed.Command.Id);
                    SessionCommands finished = sessions[completed.Command.SessionId];
                    if (finished.Commands.Count > 0) turns.Enqueue(finished);
                    else sessions.Remove(completed.Command.SessionId);
                    break;
                case SampleCpu:
                    cpuUsage.Sample();
                    foreach (CommandCpu commandCpu in cpuCommands.Values) commandCpu.Sample();
                    AssignCores();
                    break;
            }
            StartCommands();
            if (cpuCommands.Count > 0)
            {
                if (cpuSampling is null) cpuUsage.Begin();
                cpuSampling ??= new Timer(_ => changes.Add(new SampleCpu()), null, 500, 500);
            }
            else
            {
                cpuSampling?.Dispose();
                cpuSampling = null;
            }
        }
    }

    private void StartCommands()
    {
        if (allCoresInUse) return;
        int turnsToCheck = turns.Count;
        while (turnsToCheck-- > 0 && turns.TryPeek(out SessionCommands? session))
        {
            QueuedCommand next = session.Commands.Peek();
            if (next.Kind == CommandKind.Gpu && gpuInUse)
            {
                turns.Enqueue(turns.Dequeue());
                continue;
            }
            if (next.AllCores && cpuCommands.Count > 0) return;
            ulong available = cpuCores.ProcessorMask;
            foreach (CommandCpu activeCpu in cpuCommands.Values) available &= ~activeCpu.ProcessorMask;
            if (cpuCommands.Count > 0 && (available == 0 || cpuUsage.AvailableCpuCount < 1 ||
                cpuCommands.Values.Sum(commandCpu => commandCpu.RequiredCpuCount) + 1 > cpuCores.Count)) return;
            turns.Dequeue();
            QueuedCommand command = session.Commands.Dequeue();
            if (command.Kind == CommandKind.Gpu) gpuInUse = true;
            allCoresInUse = command.AllCores;
            var commandCpu = new CommandCpu();
            commandCpu.SetProcessors(available, cpuCores.CountCores(available));
            cpuCommands.Add(command.Id, commandCpu);
            var cpuRequest = commandConcurrency.AcquireAsync(1);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cpuPermits = await cpuRequest;
                    using (commandCpu) BenchmarkCommands.Execute(command, commandCpu);
                }
                finally { changes.Add(new CompletedCommand(command)); }
            });
            if (allCoresInUse) return;
        }
    }

    private void AssignCores()
    {
        if (allCoresInUse) return;
        ulong available = cpuCores.ProcessorMask;
        foreach (CommandCpu commandCpu in cpuCommands.Values) available &= ~commandCpu.ProcessorMask;
        foreach (CommandCpu commandCpu in cpuCommands.Values)
        {
            int required = Math.Clamp((int)Math.Ceiling(commandCpu.RequiredCpuCount), 1, cpuCores.Count);
            ulong previous = commandCpu.ProcessorMask;
            ulong selected = cpuCores.Select(previous, required);
            int selectedCount = cpuCores.CountCores(selected);
            if (selectedCount < required) selected |= cpuCores.Select(available, required - selectedCount);
            commandCpu.SetProcessors(selected, cpuCores.CountCores(selected));
            available = (available | previous) & ~selected;
        }
    }

    private sealed class SessionCommands
    {
        public Queue<QueuedCommand> Commands { get; } = new();
    }

    private abstract record QueueChange;
    private sealed record SubmittedCommand(QueuedCommand Command) : QueueChange;
    private sealed record CompletedCommand(QueuedCommand Command) : QueueChange;
    private sealed record SampleCpu : QueueChange;
}

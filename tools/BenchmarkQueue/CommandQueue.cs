using System.Collections.Concurrent;

namespace SomeEngine.Tools;

internal sealed class CommandQueue
{
    private readonly BlockingCollection<QueueChange> changes = new();
    private readonly Dictionary<string, SessionCommands> sessions = new();
    private readonly Queue<SessionCommands> turns = new();
    private readonly Dictionary<string, CommandCpu> localCommands = new();
    private readonly CpuCores cpuCores = new();
    private readonly CpuUsage cpuUsage = new();
    private bool gpuInUse;

    public int TotalCoreCount => cpuCores.Count;
    public void Enqueue(QueuedCommand command) => changes.Add(new SubmittedCommand(command));

    public void Run()
    {
        cpuUsage.Begin();
        using var sampling = new Timer(_ => changes.Add(new ResourceChange()), null, 2000, 2000);
        foreach (QueueChange change in changes.GetConsumingEnumerable())
        {
            switch (change)
            {
                case SubmittedCommand submitted:
                    if (!sessions.TryGetValue(submitted.Command.SessionId, out var session))
                    {
                        session = new SessionCommands();
                        sessions.Add(submitted.Command.SessionId, session);
                        turns.Enqueue(session);
                    }
                    session.Commands.Enqueue(submitted.Command);
                    break;
                case CompletedCommand completed:
                    if (localCommands.Remove(completed.Command.Id, out _)
                        && completed.Command.Kind == CommandKind.Gpu) gpuInUse = false;
                    SessionCommands finished = sessions[completed.Command.SessionId];
                    if (finished.Commands.Count > 0) turns.Enqueue(finished);
                    else sessions.Remove(completed.Command.SessionId);
                    break;
                case ResourceChange:
                    cpuUsage.Sample();
                    foreach (CommandCpu commandCpu in localCommands.Values) commandCpu.Sample();
                    break;
            }
            StartCommands();
        }
    }

    private void StartCommands()
    {
        int count = turns.Count;
        while (count-- > 0 && turns.TryPeek(out var session))
        {
            if (sessions.Count - turns.Count >= 8) return;
            QueuedCommand command = session.Commands.Peek();
            RemoteReservation? remote;
            try { remote = command.Settings.Local ? null : RemoteCommands.TryReserve(command); }
            catch (Exception error) when (!command.Settings.RemoteOnly && error is IOException or TimeoutException)
            {
                remote = null;
                command = command with { DispatchReason = "RemoteUnavailable", RemoteError = error.ToString() };
            }
            catch (Exception error)
            {
                turns.Dequeue();
                session.Commands.Dequeue();
                BenchmarkCommands.WriteDispatchFailure(command, error);
                changes.Add(new CompletedCommand(command));
                continue;
            }
            CommandCpu? processors = null;
            if (remote is null)
            {
                if (command.Settings.RemoteOnly)
                {
                    turns.Enqueue(turns.Dequeue());
                    continue;
                }
                ulong available = cpuCores.ProcessorMask;
                foreach (CommandCpu active in localCommands.Values) available &= ~active.ProcessorMask;
                if ((command.Kind == CommandKind.Gpu && gpuInUse)
                    || cpuCores.CountCores(available) < command.Settings.CpuCoreCount
                    || cpuUsage.AvailableCpuCount < command.Settings.CpuCoreCount)
                {
                    turns.Enqueue(turns.Dequeue());
                    continue;
                }
                processors = new CommandCpu(command.Settings.MemoryMiB);
                processors.SetProcessors(cpuCores.Select(available, command.Settings.CpuCoreCount), command.Settings.CpuCoreCount);
                localCommands.Add(command.Id, processors);
                if (command.Kind == CommandKind.Gpu) gpuInUse = true;
            }
            turns.Dequeue();
            session.Commands.Dequeue();
            _ = Task.Run(() =>
            {
                try
                {
                    if (remote is not null) RemoteCommands.Execute(command, remote);
                    else using (processors!) BenchmarkCommands.Execute(command, processors!);
                }
                finally { changes.Add(new CompletedCommand(command)); }
            });
        }
    }

    private sealed class SessionCommands { public Queue<QueuedCommand> Commands { get; } = new(); }
    private abstract record QueueChange;
    private sealed record SubmittedCommand(QueuedCommand Command) : QueueChange;
    private sealed record CompletedCommand(QueuedCommand Command) : QueueChange;
    private sealed record ResourceChange : QueueChange;
}

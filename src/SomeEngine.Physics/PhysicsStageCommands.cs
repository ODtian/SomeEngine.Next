using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IPhysicsStageComposition
{
    void Append(PhysicsStageCommands commands);
}

/// <summary>A stage whose Schedule records only DevicePhysicsJobs submissions; its input key identifies reused CPU recording data.</summary>
public interface IDevicePhysicsStage : IPhysicsStage
{
    object InputKey { get; }
    object? SharedInputKey => null;
    IReadOnlyList<object>? SharedInputs => null;
    bool CanBatch => true;
}

/// <summary>Groups consecutive device submissions until CPU work or reused recording inputs require completion.</summary>
public sealed class PhysicsStageCommands
{
    [ThreadStatic] internal static PhysicsStageCommands? Recording;
    private readonly PhysicsStepContext _context;
    private readonly Scheduler _scheduler;
    private JobHandle _dependency;
    private readonly List<ISubmission> _submissions = [];
    private readonly List<JobResourceAccess> _accesses = [];
    private readonly HashSet<object> _writes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _reads = new(ReferenceEqualityComparer.Instance);
    [ThreadStatic] private static HashSet<object>? s_capturedInputs;
    internal static bool CaptureInput(object input) => s_capturedInputs?.Add(input) ?? true;
    private IGraphicsBackend? _backend;
    private Device? _device;
    private SomeEngine.Graphics.Queue? _queue;

    internal PhysicsStageCommands(in PhysicsStepContext context, JobHandle dependency)
    { _context = context; _scheduler = (context.World.Execution!.Scheduler!); _dependency = dependency; }
    public void Append<T>(T stage) where T : struct, IPhysicsStage
    {
        if (stage is IPhysicsStageComposition composition) { composition.Append(this); return; }
        if (stage is IDevicePhysicsStage device && device.CanBatch)
        {
            if (HasRecordedUse(device.InputKey) || device.SharedInputKey is { } shared && HasRecordedUse(shared)
                || device.SharedInputs is { } sources && sources.Any(_writes.Contains)) Flush();
            AddInputs(device);
            PhysicsStageCommands? previous = Recording;
            Recording = this;
            try { _dependency = stage.Schedule(in _context, _dependency); }
            finally { Recording = previous; }
            AddInputs(device);
            return;
        }
        Schedule(stage);
    }
    private bool HasRecordedUse(object input) => _writes.Contains(input) || _reads.Contains(input);
    private void AddInputs(IDevicePhysicsStage stage)
    {
        _writes.Add(stage.InputKey);
        if (stage.SharedInputKey is { } input) _writes.Add(input);
        if (stage.SharedInputs is { } inputs) foreach (object source in inputs) _reads.Add(source);
    }
    public void Schedule<T>(T stage) where T : struct, IPhysicsStage
    {
        Flush();
        _dependency = stage.Schedule(in _context, _dependency);
    }
    internal JobHandle Add<T>(T work, JobResourceAccess[] accesses) where T : IDevicePhysicsSubmission
    {
        if (work.Queue is not { } queue)
        {
            Flush();
            PhysicsStageCommands? previous = Recording;
            Recording = null;
            try { _dependency = DevicePhysicsJobs.Schedule(_scheduler, work, accesses, _dependency); }
            finally { Recording = previous; }
            return _dependency;
        }
        if (_backend is not null && (!ReferenceEquals(_backend, work.Backend) || !ReferenceEquals(_device, work.Device)
            || !ReferenceEquals(_queue, queue))) Flush();
        _backend = work.Backend; _device = work.Device; _queue = queue;
        _submissions.Add(new Submission<T>(work)); _accesses.AddRange(accesses);
        return _dependency;
    }
    private void Flush()
    {
        if (_submissions.Count == 0) { _writes.Clear(); _reads.Clear(); return; }
        ISubmission[] submissions = _submissions.ToArray(); JobResourceAccess[] accesses = _accesses.ToArray();
        _dependency = _scheduler.Schedule(new SubmitAll(_scheduler, submissions, accesses), accesses, _dependency);
        _submissions.Clear(); _accesses.Clear(); _writes.Clear(); _reads.Clear(); _backend = null; _device = null; _queue = null;
    }
    internal JobHandle Complete() { Flush(); return _dependency; }
    private interface ISubmission
    {
        IGraphicsBackend Backend { get; }
        Device Device { get; }
        QueueCompletion Submit();
    }
    private readonly struct Submission<T>(T work) : ISubmission where T : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => work.Backend;
        public Device Device => work.Device;
        public QueueCompletion Submit() => work.Submit();
    }
    private readonly struct SubmitAll(Scheduler scheduler, ISubmission[] submissions, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = default;
            int submitted = 0;
            HashSet<object>? previous = s_capturedInputs;
            s_capturedInputs = new(ReferenceEqualityComparer.Instance);
            try
            {
                foreach (ISubmission submission in submissions) { completion = submission.Submit(); submitted++; }
            }
            finally
            {
                s_capturedInputs = previous;
                if (submitted != 0)
                {
                    ISubmission last = submissions[submitted - 1];
                    scheduler.CreateExternalFenceHandle(Wait(last.Backend, last.Device, completion), accesses);
                }
            }
        }
    }
    private static async ValueTask Wait(IGraphicsBackend backend, Device device, QueueCompletion completion)
    { await backend.WaitAsync(completion).ConfigureAwait(false); backend.CollectCompleted(device); }
}

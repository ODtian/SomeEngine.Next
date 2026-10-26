using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.Pose;

/// <summary>The completed Game pose and its source input, copied for the existing rendering consumer.</summary>
public readonly record struct DevicePoseOutput(Buffer Buffer, BufferRange Models, BufferRange CurrentSamples,
    BufferRange PreviousSamples, BufferRange Playback, GpuPoseTarget Input, ulong BindingVersion,
    long Number, double Time) : IComponent
{
    internal DevicePoseSources? Sources { get; init; }
}

/// <summary>Keeps the selected GPU bytes readable until their actual consumer has finished copying them.</summary>
public sealed class DevicePoseOutputRead : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly TaskCompletionSource _copied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _submitted;
    public DevicePoseOutput Output { get; }
    /// <summary>Remains true until this selected read is copied or released by its consumer.</summary>
    public bool CopyRequired => !_submitted && !_copied.Task.IsCompleted;

    internal DevicePoseOutputRead(Scheduler scheduler, in DevicePoseOutput output)
    {
        _scheduler = scheduler; Output = output;
        output.Sources?.RetainOutputRead(_copied.Task);
        scheduler.CreateExternalFenceHandle(_copied.Task, GetReadAccesses(scheduler, in output));
    }

    internal static JobResourceAccess[] GetReadAccesses(Scheduler scheduler, in DevicePoseOutput output)
    {
        JobResourceToken buffer = scheduler.GetResourceToken(output.Buffer);
        return [Read(output.Models), Read(output.CurrentSamples), Read(output.PreviousSamples), Read(output.Playback)];
        JobResourceAccess Read(BufferRange range) => JobResourceAccess.Read(buffer, (long)range.Offset, (long)range.Size);
    }

    /// <summary>Releases the read after the submitted copy's existing device completion.</summary>
    public void CompleteAfter(IGraphicsBackend backend, Device device, QueueCompletion completion)
    {
        _submitted = true;
        JobHandle copied = DevicePhysicsJobs.Schedule(_scheduler, new CopyCompletion(backend, device, completion), [], default);
        _scheduler.ScheduleFinally(new Release(this), default, copied);
    }

    public void Dispose() { if (!_submitted) _copied.TrySetResult(); }
    private readonly struct Release(DevicePoseOutputRead read) : IJob
    { public void Execute() => read._copied.TrySetResult(); }
    private readonly struct CopyCompletion(IGraphicsBackend backend, Device device, QueueCompletion completion) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => backend;
        public Device Device => device;
        public QueueCompletion Submit() => completion;
    }
}

public static partial class GpuPoseAnimation
{
    /// <summary>Retains the selected bytes while the caller reads the corresponding result component.</summary>
    public static DevicePoseOutputRead ReadDeviceOutput(World world, in DevicePoseOutput output) =>
        new(WorldJobs.GetScheduler(world), in output);

    /// <summary>Captures the selected result and retains its device read within the component read scope.</summary>
    public static DevicePoseOutputRead ReadDeviceOutput(World world, Entity target)
    {
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<DevicePoseOutput>());
        var capture = new OutputRead(target);
        world.ExecuteReadQuery(query, ref capture, (scoped QueryCursor rows, ref OutputRead capture) =>
        {
            rows.TryGetRow(capture.Target, out QueryRow row);
            capture.Read = ReadDeviceOutput(world, row.Read<DevicePoseOutput>());
        });
        world.ReleaseQuery(query);
        return capture.Read!;
    }
    private struct OutputRead(Entity target)
    {
        internal readonly Entity Target = target;
        internal DevicePoseOutputRead? Read;
    }
}

/// <summary>The accepted input clock for this target's selected device source representation.</summary>
internal struct GpuPoseSourceState : IComponent
{
    internal DevicePoseSources? Sources;
    internal PoseRange Range;
    internal int PlaybackIndex, BlendStateStart, BlendLocalStart;
    internal GpuPoseTarget Input;
    internal ulong BindingVersion;
    internal ReadOnlyMemory<short> CpuBones;
    internal ReadOnlyMemory<bool> SelectedBones;
    internal GpuPoseEvaluation? Evaluation;
    internal GpuPosePresentation Presentation;
    internal double PhysicsTime;
    internal bool HasInput, HasSamples;
}

public sealed partial class DevicePoseSources
{
    private Buffer[] _outputs = [];
    private CommandContext _outputCommands = null!;
    private readonly bool[] _outputInitialized = new bool[2];
    private int _outputIndex;
    private ulong _outputLocalCurrent, _outputLocalPrevious, _outputPlayback;
    private readonly List<Task> _outputReads = [];

    internal void RetainOutputRead(Task completed)
    {
        lock (_outputReads)
        {
            _outputReads.RemoveAll(read => read.IsCompleted);
            _outputReads.Add(completed);
        }
    }

    private void ReleaseOutputBuffers()
    {
        Buffer[] buffers = _outputs;
        foreach (Buffer buffer in buffers) _objects.Remove(buffer);
        Task[] reads;
        lock (_outputReads) reads = _outputReads.ToArray();
        Task.WhenAll(reads).ContinueWith(static (_, state) =>
        {
            foreach (Buffer buffer in (Buffer[])state!) buffer.Dispose();
        }, buffers, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public CopyDevicePoseOutput CopyOutput() => new(this);

    private void CreateOutputs(DevicePoseCapacity capacity)
    {
        _outputLocalCurrent = (ulong)capacity.Bones * 64;
        _outputLocalPrevious = _outputLocalCurrent * 2;
        _outputPlayback = _outputLocalCurrent * 3;
        ulong bytes = _outputPlayback + (ulong)capacity.Targets * 64;
        _outputs = [Keep(_backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Completed Game pose A"), MemoryType.DeviceLocal)),
            Keep(_backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Completed Game pose B"), MemoryType.DeviceLocal))];
        _outputCommands = Keep(_backend.CreateCommandContext(_device, new(QueueType.Compute, 0, 1, Label: "Copy completed Game poses")));
    }

    internal JobHandle ScheduleOutput(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        return scheduler.Schedule(new CaptureOutput(this, context.Number, context.EndTime),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
    }

    private readonly struct CaptureOutput(DevicePoseSources sources, long number, double time) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = WorldJobs.GetScheduler(sources._world);
            int index = sources._outputIndex;
            Buffer output = sources._outputs[index];
            JobResourceAccess[] accesses = [JobResourceAccess.Read(scheduler.GetResourceToken(sources)),
                sources.GetModelReadAccess(scheduler),
                JobResourceAccess.Read(scheduler.GetResourceToken(sources._historyCurrent), 0, (long)sources._historyCurrent.Info.Size),
                JobResourceAccess.Read(scheduler.GetResourceToken(sources._historyPrevious), 0, (long)sources._historyPrevious.Info.Size),
                JobResourceAccess.Read(scheduler.GetResourceToken(sources._playback), 0, (long)sources._playback.Info.Size),
                JobResourceAccess.Write(scheduler.GetResourceToken(sources._outputCommands)),
                JobResourceAccess.Write(scheduler.GetResourceToken(output), 0, (long)output.Info.Size)];
            JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new OutputCopy(sources, index), accesses, default);
            scheduler.Schedule(new WriteOutputs(sources, index, number, time),
                [JobResourceAccess.Write(scheduler.GetResourceToken(sources))], copied);
        }
    }

    private QueueCompletion SubmitOutput(int index)
    {
        Buffer output = _outputs[index];
        _backend.Begin(_outputCommands, new(0, 0, 8));
        _backend.Barrier(_outputCommands, new BufferBarrier(output,
            _outputInitialized[index] ? PipelineSync.Copy : output.InitialSync, PipelineSync.Copy,
            _outputInitialized[index] ? ResourceAccess.CopySource : output.InitialAccess, ResourceAccess.CopyDestination));
        Copy(Models, 0, 0, (ulong)_boneCount * 64);
        Copy(_historyCurrent, Models.Info.Size, _outputLocalCurrent, (ulong)_boneCount * 64);
        Copy(_historyPrevious, Models.Info.Size, _outputLocalPrevious, (ulong)_boneCount * 64);
        Copy(_playback, 0, _outputPlayback, (ulong)_order.Count * 64);
        _backend.Barrier(_outputCommands, new BufferBarrier(output, PipelineSync.Copy, PipelineSync.Copy,
            ResourceAccess.CopyDestination, ResourceAccess.CopySource));
        _outputInitialized[index] = true;
        using RecordedCommands recorded = _backend.End(_outputCommands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));

        void Copy(Buffer source, ulong start, ulong destination, ulong bytes)
        {
            _backend.Barrier(_outputCommands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            if (bytes > 0) _backend.CopyBuffer(_outputCommands, new(source, start, output, destination, bytes));
            _backend.Barrier(_outputCommands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
    }

    private readonly struct OutputCopy(DevicePoseSources sources, int index) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => sources._backend;
        public Device Device => sources._device;
        public QueueCompletion Submit() => sources.SubmitOutput(index);
    }

    private readonly struct WriteOutputs(DevicePoseSources sources, int output, long number, double time) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = WorldJobs.GetScheduler(sources._world);
            var commands = new JobCommandBuffer(sources._world, 1);
            commands.Schedule(0, new ApplyOutputs(sources, output, number, time), [JobResourceAccess.Read(scheduler.GetResourceToken(sources))]);
            JobHandle applied = commands.SchedulePlayback();
            scheduler.ScheduleFinally(new ReleasePoseCommands(commands), default, applied);
            sources._outputIndex = 1 - output;
        }
    }
    private readonly struct ApplyOutputs(DevicePoseSources sources, int output, long number, double time) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            foreach (SourceRow source in sources._order)
            {
                ulong start = (ulong)source.Range.Start * 64, bytes = (ulong)source.Range.Count * 64;
                var result = new DevicePoseOutput(sources._outputs[output], new(start, bytes),
                    new(sources._outputLocalCurrent + start, bytes), new(sources._outputLocalPrevious + start, bytes),
                    new(sources._outputPlayback + (ulong)source.Index * 64, 64), source.Input, source.Binding,
                    number, source.CommonBodies ? time : source.CalculationTime) { Sources = sources };
                commands.Replace(source.Entity, in result);
            }
        }
    }
    private readonly struct ReleasePoseCommands(JobCommandBuffer commands) : IJob
    {
        public void Execute() => commands.Dispose();
    }
}

public readonly struct CopyDevicePoseOutput(DevicePoseSources sources) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => sources.ScheduleOutput(in context, dependency);
}

public readonly struct DevicePoseOutputResources : IJobComponentResources<DevicePoseOutput>
{
    public static long GetVersion(in DevicePoseOutput component) => unchecked((long)(component.Input.Skeleton?.BindingVersion ?? 0));
    public static void Collect(in DevicePoseOutput component, ref JobResourceAccessBuilder resources)
    {
        for (SkeletonInstance? section = component.Input.Skeleton; section is not null; section = section.ParentInstance)
            resources.Read(section);
    }
}
internal readonly struct GpuPoseSourceStateResources : IJobComponentResources<GpuPoseSourceState>
{
    public static void Collect(in GpuPoseSourceState component, ref JobResourceAccessBuilder resources) { }
}

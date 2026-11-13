using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

public readonly record struct DevicePoseBodyKernels(ComputePipelineDesc WriteTargets, ComputePipelineDesc MapBones);

/// <summary>Animation targets before the shared body step, and its completed bone mapping before later rig operations.</summary>
public readonly struct DevicePoseBodyStages(DevicePoseSources sources, int targetKernel, int mapKernel)
{
    internal int TargetKernel => targetKernel;
    public WriteDevicePoseBodyTargets WriteTargets() => new(sources, targetKernel);
    public MapDevicePoseBodyBones MapBones() => new(sources, mapKernel);
}

public readonly struct WriteDevicePoseBodyTargets(DevicePoseSources sources, int kernel) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => sources.ScheduleEvaluate(in context, dependency, kernel);
}

public readonly struct MapDevicePoseBodyBones(DevicePoseSources sources, int kernel) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => sources.ScheduleBoneMapping(kernel, dependency);
}

public sealed partial class DevicePoseSources
{
    private DeviceXpbdChains<DevicePoseSources>? _chainSolver;
    private int _chainCaptureKernel, _chainMapKernel;
    int IDeviceXpbdChainInputs.WorkCount => _rigOperations.Count(operation => operation.Kernel == _xpbdKernel);
    DeviceXpbdChainWork IDeviceXpbdChainInputs.GetWork(int index)
    {
        RigOperation operation = _rigOperations.Where(operation => operation.Kernel == _xpbdKernel).ElementAt(index);
        _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(operation.Work)];
        return new(operation.Count, _bindings);
    }
    void IDeviceXpbdChainInputs.AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) =>
        accesses.AddRange(GetDeviceAccesses(scheduler, false));

    private JobHandle ScheduleChains(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_chainSolver is null)
        {
            var target = _device.Capabilities.CompileTarget;
            _chainSolver = Keep(new DeviceXpbdChains<DevicePoseSources>(_backend, _device, this, DevicePoseShaders.GetChains(target)));
            _chainCaptureKernel = AddKernel(DevicePoseShaders.GetChainTargets(target));
            _chainMapKernel = AddKernel(DevicePoseShaders.GetBodies(target).MapBones);
        }
        return ScheduleEvaluate(in context, dependency, targetKernel: -2, commonBodies: false);
    }

    public DevicePoseBodyStages CreateBodyStages(DevicePoseBodyKernels kernels)
    {
        int targets = AddKernel(kernels.WriteTargets), bones = AddKernel(kernels.MapBones);
        return new(this, targets, bones);
    }

    private int AddKernel(ComputePipelineDesc description)
    {
        int index = _pipelines.Count;
        _pipelines.Add(Keep(_backend.CreateComputePipeline(_device, description)));
        _layouts.Add(description.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        return index;
    }

    internal JobHandle ScheduleBoneMapping(int kernel, JobHandle dependency)
    {
        Scheduler scheduler = _world.Execution!.Scheduler!;
        return scheduler.Schedule(new SubmitBoneMapping(this, kernel), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
    }

    private JobResourceAccess[] GetBoneMappingAccesses(Scheduler scheduler)
    {
        var accesses = new List<JobResourceAccess>
        {
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands)),
            JobResourceAccess.Write(scheduler.GetResourceToken(Models), 0, (long)Models.Info.Size),
        };
        foreach (UploadBuffer upload in _uploads)
            accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(upload.Buffer), 0, (long)upload.Buffer.Info.Size));
        accesses.AddRange(_physics!.GetBoneMappingAccesses(scheduler));
        return accesses.ToArray();
    }

    private readonly struct SubmitBoneMapping(DevicePoseSources sources, int kernel) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = sources._world.Execution!.Scheduler!;
            DevicePhysicsJobs.Schedule(scheduler, new BodySubmission(sources, -1, kernel), sources.GetBoneMappingAccesses(scheduler), default);
        }
    }

    private readonly struct BodySubmission(DevicePoseSources sources, int targets, int bones) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => sources._backend;
        public Device Device => sources._device;
        public QueueCompletion Submit() => sources.Submit(false, targets, bones);
    }
}

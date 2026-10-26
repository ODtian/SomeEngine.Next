using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

public readonly record struct DevicePoseBodyKernels(ComputePipelineDesc WriteTargets, ComputePipelineDesc MapBones);

/// <summary>Animation targets before the shared body step, and its completed bone mapping before later rig operations.</summary>
public readonly struct DevicePoseBodyStages(DevicePoseSources sources, int targetKernel, int mapKernel)
{
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
        Scheduler scheduler = SomeEngine.ECS.Systems.WorldJobs.GetScheduler(_world);
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
            Scheduler scheduler = SomeEngine.ECS.Systems.WorldJobs.GetScheduler(sources._world);
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

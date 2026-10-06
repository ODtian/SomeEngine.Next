using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private LinkedComputePipeline? _workFeedbackPipeline;
    private GraphBufferInput _workUniforms = null!;
    private GraphBufferInput _previousWorkViewIndices = null!;
    private GraphBufferId _previousWorkCounts;
    private GraphBufferId _previousWorkPressure;
    private GraphBufferId _frameWorkPressure;
    private GraphPassId _workResetPass;
    private GraphPassId _workUpdatePass;
    private GraphPassId _workCopyPass;
    private bool _workHistoryReady;
    private readonly Dictionary<ShadowWorkView, uint> _committedWorkViews = [];
    private ShadowWorkView[] _frameWorkViews = [];

    private readonly record struct ShadowWorkView(VirtualShadowLightKind Kind, Entity Light, uint DirectLightIndex, int Detail);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    private struct WorkUniforms
    {
        public uint ViewCount;
        public uint HasPreviousWork;
        public float LoadBudget;
        public float DynamicResolutionStrength;
        public float HistoryWeight;
        public float MaxDirectionalBias;
        public float MaxLocalBias;
        public float MaxGlobalBias;
    }

    private void InstallWorkFeedback(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders,
        uint viewCapacity, GraphRegionId prepare, GraphRegionId feedback, GraphPassId resolutionCopy)
    {
        _workUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        if (!_shadowMap.Settings.WorkThrottlingEnabled) return;
        _workFeedbackPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.WorkFeedbackShader,
            shaders.WorkFeedback, "Virtual shadow per-view work feedback");
        BufferUsages uses = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination;
        _previousWorkCounts = edit.CreatePersistentBuffer(new BufferDesc((1ul + 2ul * viewCapacity) * sizeof(uint), uses,
            "Previous virtual shadow visible work"));
        _previousWorkPressure = edit.CreatePersistentBuffer(new BufferDesc((1ul + viewCapacity) * sizeof(float), uses,
            "Previous virtual shadow work pressure"));
        _frameWorkPressure = edit.CreateBuffer(new BufferDesc((1ul + viewCapacity) * sizeof(float), uses,
            "Frame virtual shadow work pressure"));
        _previousWorkViewIndices = edit.CreateBufferInput(BufferUsages.ShaderRead);
        var reset = new WorkResetData(_previousWorkCounts, _previousWorkPressure);
        _workResetPass = edit.AddCopyFramePass("Initialize virtual shadow work history", PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            WorkResetData.Declare, WorkResetData.Record, region: prepare);
        _workUpdatePass = AddComputePass(ref edit, "Apply virtual shadow per-view work pressure", _workFeedbackPipeline,
            new VirtualShadowDispatchPassData(new DispatchArguments(1, 1, 1)), prepare);
        PassDefinition pass = edit.GetPass(_workUpdatePass);
        _ = pass.Bind(_workUniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_previousWorkCounts, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_previousWorkPressure, structureStride: sizeof(float)), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_previousWorkViewIndices, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_frameResolutionBias, structureStride: 2u * sizeof(float)), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferUav(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes),
            PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete);
        _ = pass.Bind(edit.CreateBufferUav(_frameWorkPressure, structureStride: sizeof(float)),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        edit.OrderAfter(_workUpdatePass, _workResetPass);
        edit.OrderAfter(_workUpdatePass, resolutionCopy);
        edit.OrderAfter(_markPass, _workUpdatePass);
        edit.OrderAfter(_coarseMarkPass, _workUpdatePass);
        var copy = new WorkCopyData(_policyRasterWorkCount, _previousWorkCounts, _frameWorkPressure, _previousWorkPressure);
        _workCopyPass = edit.AddCopyFramePass("Carry completed virtual shadow work", PassQueueSelection.AnyOfType(QueueType.Graphics),
            copy, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            WorkCopyData.Declare, WorkCopyData.Record, region: feedback);
        edit.OrderAfter(_workCopyPass, _rasterCompletePass);
        edit.OrderAfter(_workCopyPass, _workUpdatePass);
        edit.OrderAfter(_frameCompletePass, _workCopyPass);
    }

    private void StageWorkFeedback(ref RenderGraphFrame graph, RenderLightSet lights, scoped ReadOnlySpan<VirtualShadowView> views)
    {
        if (_workFeedbackPipeline is null && _prefilterPipeline is null) return;
        _frameWorkViews = new ShadowWorkView[views.Length];
        uint[] mapping = new uint[Math.Max(views.Length, 1)];
        for (int index = 0; index < views.Length; index++)
        {
            VirtualShadowView view = views[index];
            int lightIndex = checked((int)view.LightIndex);
            Entity light = view.LightKind switch
            {
                VirtualShadowLightKind.Directional => lights.GetDirectionalEntity(lightIndex),
                VirtualShadowLightKind.Point => lights.GetPointEntity(lightIndex - lights.Directional.Count),
                _ => lights.GetSpotEntity(lightIndex - lights.Directional.Count - lights.Points.Count),
            };
            var key = new ShadowWorkView(view.LightKind, light, light == Entity.Null ? view.LightIndex : 0u,
                view.LightKind == VirtualShadowLightKind.Directional ? view.ClipmapLevel : checked((int)view.FaceIndex));
            _frameWorkViews[index] = key;
            mapping[index] = _committedWorkViews.TryGetValue(key, out uint previous) ? previous : uint.MaxValue;
        }
        if (_workFeedbackPipeline is null) return;
        graph.SetBufferInput(_previousWorkViewIndices, graph.Upload<uint>(mapping, BufferUsages.ShaderRead,
            "Virtual shadow previous work view indices"));
        var settings = _shadowMap.Settings;
        var uniforms = new WorkUniforms
        {
            ViewCount = checked((uint)views.Length),
            HasPreviousWork = _workHistoryReady && !_forceStateReset ? 1u : 0u,
            LoadBudget = settings.WorkLoadBudget > 0f ? settings.WorkLoadBudget : float.MaxValue,
            DynamicResolutionStrength = StageWorkTime(ref graph),
            HistoryWeight = settings.WorkHistoryWeight,
            MaxDirectionalBias = settings.WorkMaxDirectionalLodBias,
            MaxLocalBias = settings.WorkMaxLocalLodBias,
            MaxGlobalBias = settings.DynamicResolutionMaxLodBias,
        };
        _uniformData.Write(ref graph, _workUniforms, in uniforms);
        graph.SetPassEnabled(_workResetPass, !_workHistoryReady || _forceStateReset);
        graph.SetPassEnabled(_workUpdatePass, true);
        graph.SetPassEnabled(_workCopyPass, true);
    }

    private void CommitWorkFeedback()
    {
        if (_workFeedbackPipeline is null && _prefilterPipeline is null) return;
        _committedWorkViews.Clear();
        for (int index = 0; index < _frameWorkViews.Length; index++)
            _committedWorkViews.Add(_frameWorkViews[index], checked((uint)index));
        _workHistoryReady = true;
    }

    private readonly record struct WorkResetData(GraphBufferId Counts, GraphBufferId Pressure)
    {
        public static void Declare(ref PassDefinition pass, ref WorkResetData data)
        {
            _ = pass.Write(data.Counts, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Pressure, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        public static void Record(ref CopyPassCommandScope commands, in WorkResetData data)
        {
            var counts = commands.GetBuffer(data.Counts);
            var pressure = commands.GetBuffer(data.Pressure);
            commands.ClearBuffer(counts, new BufferRange(0, counts.Info.Size));
            commands.ClearBuffer(pressure, new BufferRange(0, pressure.Info.Size));
        }
    }

    private readonly record struct WorkCopyData(GraphBufferId Counts, GraphBufferId PreviousCounts,
        GraphBufferId Pressure, GraphBufferId PreviousPressure)
    {
        public static void Declare(ref PassDefinition pass, ref WorkCopyData data)
        {
            _ = pass.Read(data.Counts, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Read(data.Pressure, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Write(data.PreviousCounts, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.PreviousPressure, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        public static void Record(ref CopyPassCommandScope commands, in WorkCopyData data)
        {
            var counts = commands.GetBuffer(data.Counts);
            var pressure = commands.GetBuffer(data.Pressure);
            commands.CopyBuffer(new BufferCopy(counts, 0, commands.GetBuffer(data.PreviousCounts), 0, counts.Info.Size));
            commands.CopyBuffer(new BufferCopy(pressure, 0, commands.GetBuffer(data.PreviousPressure), 0, pressure.Info.Size));
        }
    }
}

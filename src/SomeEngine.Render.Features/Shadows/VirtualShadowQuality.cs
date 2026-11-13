using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Features.Shadows;

internal sealed class VirtualShadowQuality(VirtualShadowFeature feature)
{
    private readonly VirtualShadowFeature _feature = feature;

    internal GraphRegionId _qualityRegion;
    internal GraphRegionId _qualityDemandRegion;
    internal QualityReadbacks? _qualityReadbacks;
    internal GraphBufferId _qualityReceiverCoverage;
    internal GraphPassId _qualityReceiverCopyPass;
    internal uint ReceiverCoverageRowPitch => (_feature._requests.ReceiverMaskWidth * 4u + 255u) & ~255u;

    /// <summary>Installs optional quality readbacks. Ordinary throughput never calls this method.</summary>
    public void EnableQualityCapture()
    {
        ObjectDisposedException.ThrowIf(_feature._disposed, this);
        if (_qualityReadbacks is not null) return;
        var readbacks = new QualityReadbacks();
        RenderGraphEdit edit = _feature._renderGraph.BeginEdit();
        try
        {
            AddQualityReadback(ref edit, readbacks, QualityData.FrameBias, _feature._frameResolutionBias);
            // Capture the completed request count after allocation.
            GraphReadbackId demand = edit.AddReadback(_feature._requests._requestedPageCount, BufferRange.Whole,
                new QualityReceiver(readbacks, QualityData.RequestCount), "Capture frame shadow demand", _qualityDemandRegion);
            readbacks.Readbacks.Add(demand);
            edit.OrderAfter(demand, _feature._allocatePass);
            AddQualityReadback(ref edit, readbacks, QualityData.DirtyCount, _feature._activePageCount);
            AddQualityReadback(ref edit, readbacks, QualityData.Failures, _feature._allocationFailures);
            AddQualityReadback(ref edit, readbacks, QualityData.FreeCount, _feature._shadowMap.PageAssignments);
            AddQualityReadback(ref edit, readbacks, QualityData.RasterArea, _feature._policyRasterWorkCount);
            AddQualityReadback(ref edit, readbacks, QualityData.DepthWork, _feature._depthWorkCount);
            AddQualityReadback(ref edit, readbacks, QualityData.LightAssignment, _feature._lighting.Resources.AllocationStatus);
            _qualityReceiverCoverage = edit.CreateBuffer(new BufferDesc(
                (ulong)ReceiverCoverageRowPitch * _feature._requests.ReceiverMaskHeight, BufferUsages.CopyDestination | BufferUsages.CopySource,
                "Virtual shadow quality receiver cells"));
            _qualityReceiverCopyPass = edit.AddCopyFramePass("Copy virtual shadow quality receiver cells",
                PassQueueSelection.AnyOfType(QueueType.Copy),
                new ReceiverCoverageCopy(_feature._requests._pageReceiverMasks, _qualityReceiverCoverage, _feature._requests.ReceiverMaskWidth, _feature._requests.ReceiverMaskHeight, ReceiverCoverageRowPitch),
                new PassOptions(Culling: PassCullingMode.NeverCull), ReceiverCoverageCopy.Declare, ReceiverCoverageCopy.Record,
                region: _qualityRegion);
            AddQualityReadback(ref edit, readbacks, QualityData.ReceiverCoverage, _qualityReceiverCoverage);
            AddQualityReadback(ref edit, readbacks, QualityData.Views, _feature._frameShadowViews, BufferRange.Whole);
            readbacks.ActivePages = edit.CreatePersistentBuffer(new(
                (ulong)_feature._shadowMap.Settings.MaxPages * 16u,
                BufferUsages.CopyDestination | BufferUsages.CopySource, "Virtual shadow quality active pages"));
            readbacks.ActivePagesInput = edit.CreateBufferInput(BufferUsages.CopySource);
            var activePages = new ActivePageCopy(_feature._activePages, readbacks.ActivePages);
            readbacks.ActivePagesCopyPass = edit.AddCopyFramePass("Copy virtual shadow quality active pages",
                PassQueueSelection.AnyCompatible(), activePages,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                ActivePageCopy.Declare, ActivePageCopy.Record, _qualityRegion);
            edit.OrderAfter(readbacks.ActivePagesCopyPass, _feature._allocatePass);
            GraphReadbackId activeReadback = edit.AddReadback(readbacks.ActivePagesInput, BufferRange.Whole,
                new QualityReceiver(readbacks, QualityData.ActivePages), "Capture shadow quality active pages", _qualityRegion);
            readbacks.Readbacks.Add(activeReadback);
            edit.OrderAfter(activeReadback, readbacks.ActivePagesCopyPass);
            AddQualityReadback(ref edit, readbacks, QualityData.Pages, _feature._shadowMap.PageTable);
            AddQualityReadback(ref edit, readbacks, QualityData.Work, _feature._geometryWorkStatus, new BufferRange(0, 4));
            edit.Commit();
            _qualityReadbacks = readbacks;
        }
        finally { edit.Dispose(); }
    }

    internal void AddQualityReadback(ref RenderGraphEdit edit, QualityReadbacks readbacks, QualityData kind, GraphBufferId buffer)
    {
        GraphBufferInput input = edit.CreateBufferInput(BufferUsages.CopySource);
        readbacks.Inputs.Add((input, buffer));
        AddQualityReadback(ref edit, readbacks, kind, input, BufferRange.Whole);
    }

    internal void AddQualityReadback(ref RenderGraphEdit edit, QualityReadbacks readbacks, QualityData kind,
        GraphBufferInput input, BufferRange range) => readbacks.Readbacks.Add(edit.AddReadback(input, range,
            new QualityReceiver(readbacks, kind), "Capture shadow quality " + kind, _qualityRegion));

    /// <summary>Delivers semantic quality only after this frame's graph readbacks complete. Call after staging shadow views.</summary>
    public void CaptureQuality(ref RenderGraphFrame graph, Action<VirtualShadowFrameQuality> receive)
    {
        VirtualShadowFrame data = _feature.GetFrame(ref graph);
        ArgumentNullException.ThrowIfNull(receive);
        if (_qualityReadbacks is null) throw new InvalidOperationException("Enable quality capture before recording a frame.");
        data.QualityRequest = new QualityRequest(data._viewCount, data._frameBaseShadowBias, ReceiverCoverageRowPitch / 4u, receive);
        foreach (var input in _qualityReadbacks.Inputs)
            graph.SetBufferInput(input.Input, new(input.Buffer, BufferRange.Whole));
        graph.SetBufferInput(_qualityReadbacks.ActivePagesInput, new(_qualityReadbacks.ActivePages, BufferRange.Whole));
        graph.SetPassEnabled(_qualityReadbacks.ActivePagesCopyPass, true);
        graph.SetPassDataIfChanged(_qualityReceiverCopyPass, new ReceiverCoverageCopy(_feature._requests._pageReceiverMasks,
            _qualityReceiverCoverage, _feature._requests.ReceiverMaskWidth, _feature._requests.ReceiverMaskHeight, ReceiverCoverageRowPitch));
        graph.SetPassEnabled(_qualityReceiverCopyPass, true);
        foreach (GraphReadbackId readback in _qualityReadbacks.Readbacks)
        {
            graph.SetReadbackEnabled(readback, true);
            graph.SetReadbackTag(readback, data._frameIndex);
        }
    }

    internal enum QualityData { FrameBias, RequestCount, DirtyCount, Failures, FreeCount, RasterArea, ReceiverCoverage, Views, Pages, Work, DepthWork, LightAssignment, ActivePages, Count }

    internal sealed class QualityReadbacks
    {
        internal readonly List<GraphReadbackId> Readbacks = [];
        internal readonly Dictionary<ulong, QualityRequest> Requests = [];
        internal readonly List<(GraphBufferInput Input, GraphBufferId Buffer)> Inputs = [];
        internal GraphBufferId ActivePages;
        internal GraphBufferInput ActivePagesInput = null!;
        internal GraphPassId ActivePagesCopyPass;
    }

    internal sealed class QualityReceiver(QualityReadbacks readbacks, QualityData kind) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (!readbacks.Requests.TryGetValue(tag, out QualityRequest? request)) return;
            request.Bytes[(int)kind] = bytes.ToArray();
            if (++request.Received != request.Bytes.Length) return;
            readbacks.Requests.Remove(tag);
            request.Receive(request.Decode(tag));
        }
    }

    internal sealed class QualityRequest(int viewCount, float[] baseBias, uint coverageRowWords, Action<VirtualShadowFrameQuality> receive)
    {
        private readonly float[] _baseBias = baseBias.AsSpan(0, viewCount).ToArray();
        internal readonly byte[][] Bytes = new byte[(int)QualityData.Count][];
        internal readonly Action<VirtualShadowFrameQuality> Receive = receive;
        internal int Received;

        internal VirtualShadowFrameQuality Decode(ulong frame)
        {
            ReadOnlySpan<float> bias = MemoryMarshal.Cast<byte, float>(Bytes[(int)QualityData.FrameBias]);
            ReadOnlySpan<VirtualShadowView> views = MemoryMarshal.Cast<byte, VirtualShadowView>(Bytes[(int)QualityData.Views]).Slice(0, viewCount);
            ReadOnlySpan<uint> pages = MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.Pages]);
            ReadOnlySpan<uint> coverage = MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.ReceiverCoverage]);
            ReadOnlySpan<uint> activePages = MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.ActivePages]);
            var result = new VirtualShadowViewQuality[views.Length];
            for (int index = 0; index < views.Length; index++)
            {
                VirtualShadowView view = views[index];
                uint dirtyPages = 0;
                for (int active = 0; active < Word(QualityData.DirtyCount); active++)
                {
                    uint tableIndex = activePages[active * 4 + 1];
                    if (tableIndex >= view.PageTableOffset && tableIndex - view.PageTableOffset < view.PageTableEntryCount)
                        dirtyPages++;
                }
                var mips = new VirtualShadowMipQuality[view.MipLevelCount];
                uint dimension = view.VirtualResolution / view.PageSize;
                uint offset = view.PageTableOffset;
                for (uint mip = 0; mip < view.MipLevelCount; mip++)
                {
                    uint mapped = 0, whole = 0, partial = 0, unfinished = 0;
                    ulong covered = 0;
                    int originX = view.VirtualPageOriginX >> (int)mip, originY = view.VirtualPageOriginY >> (int)mip;
                    bool singlePage = (view.Flags & VirtualShadowViewFlags.SinglePage) != 0u;
                    uint mipPages = singlePage ? (mip == view.MipLevelCount - 1u ? 1u : 0u) : dimension * dimension;
                    for (uint page = 0; page < mipPages; page++)
                    {
                        uint table = offset + page;
                        uint entry = pages[checked((int)table)];
                        if ((entry & 0x40000000u) == 0u) continue;
                        mapped++;
                        if ((entry & 0x80000000u) == 0u) { unfinished++; continue; }
                        if ((entry & 0x04000000u) == 0u) { whole++; covered += 64; }
                        else
                        {
                            partial++;
                            uint mask = dimension - 1u;
                            uint x = ((page % dimension) - unchecked((uint)originX)) & mask;
                            uint y = ((page / dimension) - unchecked((uint)originY)) & mask;
                            uint basePages = view.VirtualResolution / view.PageSize;
                            uint tailX = mip == 0u ? 0u : basePages - (basePages >> ((int)mip - 1));
                            uint tailY = mip == 0u ? 0u : basePages;
                            uint address = view.ReceiverMaskOriginX + tailX + x +
                                (view.ReceiverMaskOriginY + 2u * (tailY + y)) * coverageRowWords;
                            covered += (uint)(BitOperations.PopCount(coverage[(int)address]) +
                                BitOperations.PopCount(coverage[(int)(address + coverageRowWords)]));
                        }
                    }
                    mips[mip] = new(mip, Math.Max(view.VirtualResolution >> (int)mip, view.PageSize),
                        mapped, whole, partial, unfinished, covered);
                    offset += mipPages;
                    dimension = Math.Max(dimension >> 1, 1);
                }
                result[index] = new(view.LightKind, view.LightIndex, view.FaceIndex, view.ClipmapLevel, view.VirtualResolution,
                    _baseBias[index], view.ResolutionLodBias + bias[0], mips)
                {
                    DirtyPages = dirtyPages,
                    HistoryValid = (view.Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u,
                    Projection = view,
                    WorkLodAddition = view.ResolutionLodBias - _baseBias[index],
                    HardwareVisibleItems = Word(QualityData.RasterArea, 1 + index * 2),
                    SoftwareVisibleItems = Word(QualityData.RasterArea, 2 + index * 2),
                    DynamicHardwareVisibleItems = Word(QualityData.DepthWork, index * 4),
                    DynamicSoftwareVisibleItems = Word(QualityData.DepthWork, index * 4 + 1),
                    StaticHardwareVisibleItems = Word(QualityData.DepthWork, index * 4 + 2),
                    StaticSoftwareVisibleItems = Word(QualityData.DepthWork, index * 4 + 3),
                };
            }
            return new(frame, bias[0], bias[0], Word(QualityData.RequestCount), Word(QualityData.DirtyCount),
                Word(QualityData.Failures), Word(QualityData.Failures, 1), FreePages(),
                Word(QualityData.RasterArea), (RenderGeometryWorkFlags)Word(QualityData.Work), result)
            { LightAssignmentOverflow = Word(QualityData.LightAssignment) != 0u };
        }

        private uint FreePages()
        {
            uint count = 0u;
            foreach (uint page in MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.FreeCount]))
                if (page == uint.MaxValue) count++;
            return count;
        }

        private uint Word(QualityData kind, int word = 0) => MemoryMarshal.Cast<byte, uint>(Bytes[(int)kind])[word];
    }

    private readonly record struct ActivePageCopy(GraphBufferId Source, GraphBufferId Destination)
    {
        internal static void Declare(ref PassDefinition pass, ref ActivePageCopy data)
        {
            pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource,
                contentScope: BufferContentScope.CountBoundedRanges);
            pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref CopyPassCommandScope commands, in ActivePageCopy data)
        {
            var source = commands.GetBuffer(data.Source);
            commands.CopyBuffer(new(source, 0, commands.GetBuffer(data.Destination), 0, source.Info.Size));
        }
    }

    internal readonly record struct ReceiverCoverageCopy(GraphTextureId Source, GraphBufferId Destination,
        uint Width, uint Height, uint RowPitch)
    {
        internal static void Declare(ref PassDefinition pass, ref ReceiverCoverageCopy data)
        {
            _ = pass.Read(data.Source, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
            _ = pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        internal static void Record(ref CopyPassCommandScope commands, in ReceiverCoverageCopy data) =>
            commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Destination), 0,
                data.RowPitch, data.Height, commands.GetTexture(data.Source), 0, 0, TextureAspects.Color,
                0, 0, 0, data.Width, data.Height, 1));
    }


    /// <summary>Completed asynchronous page-pressure LOD bias, tagged with its RenderGraph frame.</summary>
    public event Action<ulong, float>? ResolutionBiasCompleted;

    internal GraphReadbackId _distantBiasReadback;
    internal readonly RenderFeedback<VirtualShadowQualitySample> Feedback = new();

    internal void ReconcileFeedback(ulong completedFrame)
    {
        while (Feedback.TryRead(completedFrame, out var result))
        {
            VirtualShadowQualitySample sample = result.Value;
            if (sample.Kind == VirtualShadowQualityKind.ResolutionBias)
            {
                _feature._viewEntities.UpdateGlobalLocalResolutionLodBias(sample.Value);
                ResolutionBiasCompleted?.Invoke(result.FrameIdentity, sample.Value);
                continue;
            }
            if (!_workTimeFractions.Remove(result.FrameIdentity, out float fraction)) continue;
            if (_ignoreFirstWorkTime) { _ignoreFirstWorkTime = false; continue; }
            if (_workTimeSamples.Count == 16) _workTimeSamples.RemoveAt(0);
            _workTimeSamples.Add(new WorkTimeSample(sample.Value, fraction));
        }
    }

    internal void SubmitCapture(VirtualShadowFrame data)
    {
        if (data.QualityRequest is { } request) _qualityReadbacks!.Requests.Add(data._frameIndex, request);
    }

    internal void InstallDistantBias(ref RenderGraphEdit edit, GraphRegionId feedback)
    {
        _distantBiasReadback = edit.AddReadback(_feature._dynamicResolutionBias, BufferRange.Whole, new DistantBiasReceiver(this),
            "Observe local shadow page pressure", feedback);
        edit.OrderAfter(_distantBiasReadback, _feature._dynamicResolutionPass);
    }

    internal void StageDistantBias(ref RenderGraphFrame graph)
    {
        if (!_feature._shadowMap.Settings.DynamicResolutionEnabled)
        {
            graph.SetReadbackEnabled(_distantBiasReadback, false);
            return;
        }
        graph.SetReadbackTag(_distantBiasReadback, graph.FrameIdentity);
        graph.SetReadbackEnabled(_distantBiasReadback, true);
    }

    internal sealed class DistantBiasReceiver(VirtualShadowQuality feature) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            var sample = new VirtualShadowQualitySample(VirtualShadowQualityKind.ResolutionBias,
                MemoryMarshal.Cast<byte, float>(bytes)[0]);
            feature.Feedback.Write(tag, in sample);
        }
    }


    internal LinkedComputePipeline? _workFeedbackPipeline;
    internal GraphBufferInput _workUniforms = null!;
    internal GraphBufferInput _previousWorkViewIndices = null!;
    internal GraphBufferId _previousWorkCounts;
    internal GraphBufferId _previousWorkPressure;
    internal GraphBufferId _frameWorkPressure;
    internal GraphPassId _workResetPass;
    internal GraphPassId _workUpdatePass;
    internal GraphPassId _workCopyPass;
    internal bool _workHistoryReady;
    internal readonly Dictionary<ShadowWorkView, uint> _historyWorkViews = [];

    internal readonly record struct ShadowWorkView(VirtualShadowLightKind Kind, Entity Light, uint DirectLightIndex, int Detail, bool FirstPerson);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    internal struct WorkUniforms
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

    internal void InstallWorkFeedback(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders,
        uint viewCapacity, GraphRegionId prepare, GraphRegionId feedback)
    {
        _workUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _workFeedbackPipeline = LinkedComputePipeline.Create(_feature._renderPipelines,shaders.WorkFeedbackShader,            shaders.WorkFeedback,"Virtual shadow per-view work feedback");
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
        _workUpdatePass = _feature.AddComputePass(ref edit, "Apply virtual shadow per-view work pressure", _workFeedbackPipeline,
            new VirtualShadowDispatchPassData(new DispatchArguments(1, 1, 1)), prepare);
        const PipelineSync sync = PipelineSync.ComputeShading;
        _feature.BindComputeResources(ref edit, _workUpdatePass, new()
        {
            ["WorkThrottle"] = GraphParameterResourceBinding.ConstantBuffer(_workUniforms, sync),
            ["PreviousWorkCounts"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(
                _previousWorkCounts, structureStride: sizeof(uint)), sync),
            ["PreviousWorkPressure"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(
                _previousWorkPressure, structureStride: sizeof(float)), sync),
            ["PreviousWorkViewIndices"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(
                _previousWorkViewIndices, structureStride: sizeof(uint)), sync),
            ["FramePageBias"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(
                _feature._frameResolutionBias, structureStride: sizeof(float)), sync),
            ["WorkShadowViews"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(
                _feature._frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync,
                GraphAccessMode.ReadWrite, WriteCoverage.Complete),
            ["FrameWorkPressure"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(
                _frameWorkPressure, structureStride: sizeof(float)), sync, GraphAccessMode.Write, WriteCoverage.Complete),
        });
        edit.OrderAfter(_workUpdatePass, _feature._resolutionCopyPass);
        edit.OrderAfter(_workUpdatePass, _workResetPass);
        edit.OrderAfter(_feature._coarseMarkPass, _workUpdatePass);
        var copy = new WorkCopyData(_feature._policyRasterWorkCount, _previousWorkCounts, _frameWorkPressure, _previousWorkPressure);
        _workCopyPass = edit.AddCopyFramePass("Carry completed virtual shadow work", PassQueueSelection.AnyOfType(QueueType.Graphics),
            copy, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            WorkCopyData.Declare, WorkCopyData.Record, region: feedback);
        edit.OrderAfter(_workCopyPass, _feature._rasterCompletePass);
        edit.OrderAfter(_workCopyPass, _workUpdatePass);
        edit.OrderAfter(_feature._frameCompletePass, _workCopyPass);
    }

    internal void StageWorkFeedback(ref RenderGraphFrame graph, RenderLightSet lights, scoped ReadOnlySpan<VirtualShadowView> views)
    {
        VirtualShadowFrame data = _feature.GetFrame(ref graph);
        if (_workFeedbackPipeline is null && _feature._prefilter._prefilterPipeline is null) return;
        data._frameWorkViews = new ShadowWorkView[views.Length];
        for (int index = 0; index < views.Length; index++)
        {
            VirtualShadowView view = views[index];
            int lightIndex = checked((int)view.LightIndex);
            Entity light = view.LightKind switch
            {
                VirtualShadowLightKind.Directional => lights.GetDirectionalEntity(lightIndex),
                VirtualShadowLightKind.Point => lights.GetPointEntity(lights.UploadOrder[lightIndex - lights.Directional.Count].Index),
                _ => lights.GetSpotEntity(lights.UploadOrder[lightIndex - lights.Directional.Count].Index),
            };
            var key = new ShadowWorkView(view.LightKind, light, light == Entity.Null ? view.LightIndex : 0u,
                view.LightKind == VirtualShadowLightKind.Directional ? view.ClipmapLevel : checked((int)view.FaceIndex),
                (view.Flags & VirtualShadowViewFlags.FirstPersonShadow) != 0u);
            data._frameWorkViews[index] = key;
        }
        if (_workFeedbackPipeline is null) return;
        graph.SetBufferInput(_previousWorkViewIndices, graph.Upload(Math.Max(views.Length, 1) * sizeof(uint),
            BufferUsages.ShaderRead, (Frame: data, History: _historyWorkViews), static (bytes, source) =>
            {
                Span<uint> mapping = MemoryMarshal.Cast<byte, uint>(bytes);
                mapping.Fill(uint.MaxValue);
                for (int index = 0; index < source.Frame._frameWorkViews.Length; index++)
                    if ((source.Frame.Views.Views[index].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u &&
                        source.History.TryGetValue(source.Frame._frameWorkViews[index], out uint previous))
                        mapping[index] = previous;
            }, "Virtual shadow previous work view indices"));
        var settings = _feature._shadowMap.Settings;
        var uniforms = new WorkUniforms
        {
            ViewCount = checked((uint)views.Length),
            HasPreviousWork = _workHistoryReady && !_feature._shadowMap.RequiresInitialization ? 1u : 0u,
            LoadBudget = settings.WorkLoadBudget > 0f ? settings.WorkLoadBudget : float.MaxValue,
            DynamicResolutionStrength = StageWorkTime(ref graph),
            HistoryWeight = settings.WorkHistoryWeight,
            MaxDirectionalBias = settings.WorkMaxDirectionalLodBias,
            MaxLocalBias = settings.WorkMaxLocalLodBias,
            MaxGlobalBias = settings.DynamicResolutionMaxLodBias,
        };
        _feature._uniformData.Write(ref graph, _workUniforms, in uniforms);
        graph.SetPassEnabled(_workResetPass, !_workHistoryReady || _feature._shadowMap.RequiresInitialization);
        graph.SetPassEnabled(_workUpdatePass, true);
        graph.SetPassEnabled(_workCopyPass, true);
    }

    internal void CommitWorkFeedback(VirtualShadowFrame data)
    {
        if (_workFeedbackPipeline is null && _feature._prefilter._prefilterPipeline is null) return;
        _historyWorkViews.Clear();
        for (int index = 0; index < data._frameWorkViews.Length; index++)
            _historyWorkViews.Add(data._frameWorkViews[index], checked((uint)index));
        _workHistoryReady = true;
    }

    internal readonly record struct WorkResetData(GraphBufferId Counts, GraphBufferId Pressure)
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

    internal readonly record struct WorkCopyData(GraphBufferId Counts, GraphBufferId PreviousCounts,
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


    internal QueryPool? _workTimeQueries;
    internal GraphPassId _workTimeEndPass;
    internal GraphPassId _workTimeStartPass;
    internal GraphPassId _workTimeResolvePass;
    internal GraphReadbackId _workTimeReadback;
    internal ulong _workTimeFrequency;
    internal float _workTimeFraction = 1f;
    internal int _workTimeFramesSinceChange;
    internal bool _ignoreFirstWorkTime = true;
    internal readonly List<WorkTimeSample> _workTimeSamples = [];
    internal readonly Dictionary<ulong, float> _workTimeFractions = [];
    internal readonly record struct WorkTimeSample(float Milliseconds, float Fraction);

    internal void InstallWorkTime(ref RenderGraphEdit edit, GraphRegionId prepare, GraphRegionId finish, GraphRegionId feedback)
    {
        _workTimeFrequency = _feature._backend.CalibrateTimestamps(_feature._backend.GetQueue(_feature._device, QueueType.Graphics)).QueueFrequency;
        _workTimeQueries = _feature._backend.CreateQueryPool(_feature._device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2,
            Label: "Virtual shadow geometry time"));
        GraphQueryPoolId queries = edit.RegisterQueryPool(_workTimeQueries);
        GraphBufferId results = edit.CreateBuffer(new BufferDesc(2u * sizeof(ulong), BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Virtual shadow geometry timestamps"));
        GraphPassId start = edit.AddGeneralFramePass("Start virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestamp(queries, 0), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestamp.Declare, WorkTimestamp.Record, region: prepare);
        _workTimeStartPass = start;
        edit.OrderAfter(start, _feature._clearPass);
        GraphPassId end = edit.AddGeneralFramePass("End virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestamp(queries, 1), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestamp.Declare, WorkTimestamp.Record, region: finish);
        _workTimeEndPass = end;
        edit.OrderAfter(end, _feature._rasterCompletePass);
        GraphPassId resolve = edit.AddGeneralFramePass("Resolve virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestampResults(queries, results), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestampResults.Declare, WorkTimestampResults.Record, region: feedback);
        _workTimeResolvePass = resolve;
        edit.OrderAfter(resolve, end);
        edit.OrderAfter(_feature._frameCompletePass, resolve);
        _workTimeReadback = edit.AddReadback(results, BufferRange.Whole, new WorkTimeReceiver(this),
            "Observe virtual shadow geometry time", feedback);
        edit.OrderAfter(_workTimeReadback, resolve);
    }

    internal float StageWorkTime(ref RenderGraphFrame graph)
    {
        VirtualShadowFrame data = _feature.GetFrame(ref graph);
        bool enabled = _feature._shadowMap.Settings.WorkTimeBudgetMilliseconds > 0f;
        graph.SetPassEnabled(_workTimeStartPass, enabled);
        graph.SetPassEnabled(_workTimeEndPass, enabled);
        graph.SetPassEnabled(_workTimeResolvePass, enabled);
        graph.SetReadbackEnabled(_workTimeReadback, enabled);
        if (!enabled) return -1f;
        RefreshWorkTimeFraction(data);
        data.ReadWorkTime = true;
        graph.SetReadbackTag(_workTimeReadback, graph.FrameIdentity);
        graph.SetReadbackEnabled(_workTimeReadback, true);
        return Math.Clamp(1f - data._frameWorkTimeFraction, 0f, 1f);
    }

    internal void RefreshWorkTimeFraction(VirtualShadowFrame data)
    {
        data._frameWorkTimeFraction = _workTimeFraction;
        data._frameWorkTimeFramesSinceChange = _workTimeFramesSinceChange;
        data._frameWorkTimeClearsHistory = false;
        if (_workTimeSamples.Count == 0) return;
        float budget = _feature._shadowMap.Settings.WorkTimeBudgetMilliseconds;
        float target = budget * .95f;
        float sum = 0f, weightSum = 0f, weight = 1f;
        int consecutiveOverBudget = 0, count = 0;
        for (int index = _workTimeSamples.Count - 1; index >= 0; index--)
        {
            WorkTimeSample sample = _workTimeSamples[index];
            consecutiveOverBudget = sample.Milliseconds > budget ? consecutiveOverBudget + 1 : 0;
            sum += target / sample.Milliseconds * sample.Fraction * weight;
            weightSum += weight;
            count++;
            weight *= .9f;
            if (consecutiveOverBudget == 2) break;
        }
        float next = sum / weightSum;
        bool allOverBudget = count > 0 && consecutiveOverBudget == count;
        bool canChange = _workTimeFramesSinceChange >= 8;
        if (allOverBudget) data._frameWorkTimeClearsHistory = true;
        else
        {
            bool enough = canChange && (next > _workTimeFraction * 1.01f || next < _workTimeFraction * .99f ||
                (next != _workTimeFraction && (next == 0f || next == 1f)));
            if (!enough) next = _workTimeFraction;
            if (next > _workTimeFraction) next = float.Lerp(_workTimeFraction, next, .9f);
        }
        next = Math.Clamp(next, 0f, 1f);
        if (next != _workTimeFraction && canChange)
        {
            data._frameWorkTimeFraction = next;
            data._frameWorkTimeFramesSinceChange = 0;
        }
        else data._frameWorkTimeFramesSinceChange++;
    }

    internal void CommitWorkTime(VirtualShadowFrame data, ulong frameIdentity)
    {
        if (!data.ReadWorkTime) return;
        _workTimeFraction = data._frameWorkTimeFraction;
        _workTimeFramesSinceChange = data._frameWorkTimeFramesSinceChange;
        if (data._frameWorkTimeClearsHistory) _workTimeSamples.Clear();
        _workTimeFractions.Add(frameIdentity, data._frameWorkTimeFraction);
    }

    internal sealed class WorkTimeReceiver(VirtualShadowQuality feature) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            ReadOnlySpan<ulong> timestamps = MemoryMarshal.Cast<byte, ulong>(bytes);
            var sample = new VirtualShadowQualitySample(VirtualShadowQualityKind.WorkTime,
                (float)(1000.0 * (timestamps[1] - timestamps[0]) / feature._workTimeFrequency));
            feature.Feedback.Write(tag, in sample);
        }
    }

    internal readonly record struct WorkTimestamp(GraphQueryPoolId Queries, uint Index)
    {
        public static void Declare(ref PassDefinition pass, ref WorkTimestamp data) => pass.Write(data.Queries, new QueryRange(data.Index, 1));
        public static void Record(ref GeneralPassCommandScope commands, in WorkTimestamp data) => commands.WriteTimestamp(data.Queries, data.Index);
    }

    internal readonly record struct WorkTimestampResults(GraphQueryPoolId Queries, GraphBufferId Results)
    {
        public static void Declare(ref PassDefinition pass, ref WorkTimestampResults data)
        {
            pass.Read(data.Queries, new QueryRange(0, 2));
            _ = pass.Write(data.Results, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        public static void Record(ref GeneralPassCommandScope commands, in WorkTimestampResults data) =>
            commands.ResolveQueries(data.Queries, 0, 2, commands.GetBuffer(data.Results), new BufferRange(0, 2u * sizeof(ulong)));
    }

}

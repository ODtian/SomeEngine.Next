using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private readonly GraphRegionId _qualityRegion;
    private readonly GraphRegionId _qualityDemandRegion;
    private QualityReadbacks? _qualityReadbacks;
    private GraphBufferId _qualityReceiverCoverage;
    private GraphPassId _qualityReceiverCopyPass;
    private uint ReceiverCoverageRowPitch => (ReceiverMaskWidth * 4u + 255u) & ~255u;

    /// <summary>Installs optional quality readbacks. Ordinary throughput never calls this method.</summary>
    public void EnableQualityCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_qualityReadbacks is not null) return;
        var readbacks = new QualityReadbacks();
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            AddQualityReadback(ref edit, readbacks, QualityData.FrameBias, _frameResolutionBias);
            // Capture the completed request count after allocation.
            GraphReadbackId demand = edit.AddReadback(_requestedPageCount, BufferRange.Whole,
                new QualityReceiver(readbacks, QualityData.RequestCount), "Capture frame shadow demand", _qualityDemandRegion);
            readbacks.Readbacks.Add(demand);
            edit.OrderAfter(demand, _allocatePass);
            AddQualityReadback(ref edit, readbacks, QualityData.DirtyCount, _activePageCount);
            AddQualityReadback(ref edit, readbacks, QualityData.Failures, _allocationFailures);
            AddQualityReadback(ref edit, readbacks, QualityData.FreeCount, _pageOwners);
            AddQualityReadback(ref edit, readbacks, QualityData.RasterArea, _policyRasterWorkCount);
            AddQualityReadback(ref edit, readbacks, QualityData.DepthWork, _depthWorkCount);
            AddQualityReadback(ref edit, readbacks, QualityData.LightAssignment, _lighting.Resources.AllocationStatus);
            _qualityReceiverCoverage = edit.CreateBuffer(new BufferDesc(
                (ulong)ReceiverCoverageRowPitch * ReceiverMaskHeight, BufferUsages.CopyDestination | BufferUsages.CopySource,
                "Virtual shadow quality receiver cells"));
            _qualityReceiverCopyPass = edit.AddCopyFramePass("Copy virtual shadow quality receiver cells",
                PassQueueSelection.AnyOfType(QueueType.Copy),
                new ReceiverCoverageCopy(_pageReceiverMasks, _qualityReceiverCoverage, ReceiverMaskWidth, ReceiverMaskHeight, ReceiverCoverageRowPitch),
                new PassOptions(Culling: PassCullingMode.NeverCull), ReceiverCoverageCopy.Declare, ReceiverCoverageCopy.Record,
                region: _qualityRegion);
            AddQualityReadback(ref edit, readbacks, QualityData.ReceiverCoverage, _qualityReceiverCoverage);
            AddQualityReadback(ref edit, readbacks, QualityData.Views, _frameShadowViews, BufferRange.Whole);
            AddQualityReadback(ref edit, readbacks, QualityData.Pages, _framePageTable, BufferRange.Whole);
            AddQualityReadback(ref edit, readbacks, QualityData.Work, _geometryWorkStatus, new BufferRange(0, 4));
            edit.Commit();
            _qualityReadbacks = readbacks;
        }
        finally { edit.Dispose(); }
    }

    private void AddQualityReadback(ref RenderGraphEdit edit, QualityReadbacks readbacks, QualityData kind, GraphBufferId buffer) =>
        readbacks.Readbacks.Add(edit.AddReadback(buffer, BufferRange.Whole, new QualityReceiver(readbacks, kind),
            "Capture shadow quality " + kind, _qualityRegion));

    private void AddQualityReadback(ref RenderGraphEdit edit, QualityReadbacks readbacks, QualityData kind,
        GraphBufferInput input, BufferRange range) => readbacks.Readbacks.Add(edit.AddReadback(input, range,
            new QualityReceiver(readbacks, kind), "Capture shadow quality " + kind, _qualityRegion));

    /// <summary>Delivers semantic quality only after this frame's graph readbacks complete. Call after staging shadow views.</summary>
    public void CaptureQuality(ref RenderGraphFrame graph, Action<VirtualShadowFrameQuality> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        if (_qualityReadbacks is null) throw new InvalidOperationException("Enable quality capture before recording a frame.");
        _qualityReadbacks.Requests.Add(_frameIndex, new QualityRequest(_viewCount, _frameBaseShadowBias, ReceiverCoverageRowPitch / 4u, receive));
        graph.SetPassDataIfChanged(_qualityReceiverCopyPass, new ReceiverCoverageCopy(_pageReceiverMasks,
            _qualityReceiverCoverage, ReceiverMaskWidth, ReceiverMaskHeight, ReceiverCoverageRowPitch));
        graph.SetPassEnabled(_qualityReceiverCopyPass, true);
        foreach (GraphReadbackId readback in _qualityReadbacks.Readbacks)
        {
            graph.SetReadbackEnabled(readback, true);
            graph.SetReadbackTag(readback, _frameIndex);
        }
    }

    private enum QualityData { FrameBias, RequestCount, DirtyCount, Failures, FreeCount, RasterArea, ReceiverCoverage, Views, Pages, Work, DepthWork, LightAssignment }

    private sealed class QualityReadbacks
    {
        internal readonly List<GraphReadbackId> Readbacks = [];
        internal readonly Dictionary<ulong, QualityRequest> Requests = [];
    }

    private sealed class QualityReceiver(QualityReadbacks readbacks, QualityData kind) : IGraphReadbackReceiver
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

    private sealed class QualityRequest(int viewCount, float[] baseBias, uint coverageRowWords, Action<VirtualShadowFrameQuality> receive)
    {
        internal readonly byte[][] Bytes = new byte[12][];
        internal readonly Action<VirtualShadowFrameQuality> Receive = receive;
        internal int Received;

        internal VirtualShadowFrameQuality Decode(ulong frame)
        {
            ReadOnlySpan<float> bias = MemoryMarshal.Cast<byte, float>(Bytes[(int)QualityData.FrameBias]);
            ReadOnlySpan<VirtualShadowView> views = MemoryMarshal.Cast<byte, VirtualShadowView>(Bytes[(int)QualityData.Views]).Slice(0, viewCount);
            ReadOnlySpan<uint> pages = MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.Pages]);
            ReadOnlySpan<uint> coverage = MemoryMarshal.Cast<byte, uint>(Bytes[(int)QualityData.ReceiverCoverage]);
            var result = new VirtualShadowViewQuality[views.Length];
            for (int index = 0; index < views.Length; index++)
            {
                VirtualShadowView view = views[index];
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
                    baseBias[index], view.ResolutionLodBias + bias[0], mips)
                {
                    WorkLodAddition = view.ResolutionLodBias - baseBias[index],
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
                Word(QualityData.RasterArea), (ClusterWorkOverflow)Word(QualityData.Work), result)
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

    private readonly record struct ReceiverCoverageCopy(GraphTextureId Source, GraphBufferId Destination,
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
}

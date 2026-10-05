using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct TargetSourceBatch(ClusterTargetBatchUniforms Values, uint[] MaterialBins, uint DrawingVersion);
    private sealed class TargetSourceBatchComparer : IEqualityComparer<TargetSourceBatch>
    {
        public bool Equals(TargetSourceBatch first, TargetSourceBatch second) => first.Values.Equals(second.Values) &&
            first.DrawingVersion == second.DrawingVersion && first.MaterialBins.AsSpan().SequenceEqual(second.MaterialBins);
        public int GetHashCode(TargetSourceBatch value) => HashCode.Combine(value.Values, value.DrawingVersion);
    }
    private KeyValuePair<uint, TargetSourceBatch>[] _targetBatches = [];
    private bool _targetBatchesChanged;
    private ulong _targetBatchSourceCount;
    private uint _targetBatchViewCount;
    private uint _targetDrawingVersion;
    private uint _targetBatchDrawingVersion;
    private ulong _targetBatchMaterialVersion;
    private RenderInstanceBatch? _targetBatchInstances;

    private readonly record struct TargetDrawingStep(GraphPassId Pass, uint[] MaterialBins);
    private RasterPhase? _targetFirstRaster;
    private RasterPhase? _targetSecondRaster;
    private TargetDrawingStep[] _targetFirstDrawing = [];
    private TargetDrawingStep[] _targetSecondDrawing = [];

    private TargetDrawingStep[] GetTargetDrawingSteps(ref RenderGraphFrame graph, bool first)
    {
        RasterPhase raster = GraphData.RasterPhases[first ? 2 : 3];
        if (ReferenceEquals(raster, first ? _targetFirstRaster : _targetSecondRaster))
            return first ? _targetFirstDrawing : _targetSecondDrawing;
        var materialBins = new Dictionary<GraphPassId, uint[]>();
        foreach (RasterBin bin in raster.Bins.Values)
            if (raster.Bins.TryGetRegion(bin.Stage.BinIndex, out GraphRegionId region))
                foreach (GraphPassId pass in graph.GetRegionPasses(region))
                    materialBins.Add(pass, [bin.Stage.BinIndex]);
        foreach (TessellationGroup group in raster.TessellationGroups.Values)
        {
            uint[] bins = group.Seeds.Values.Select(static bin => bin.Stage.BinIndex).ToArray();
            foreach (RasterBin bin in group.Seeds.Values)
                if (group.Seeds.TryGetRegion(bin.Stage.BinIndex, out GraphRegionId region))
                    foreach (GraphPassId pass in graph.GetRegionPasses(region))
                        materialBins.Add(pass, [bin.Stage.BinIndex]);
            if (group.Split.TryGetRegion(0, out GraphRegionId split))
                foreach (GraphPassId pass in graph.GetRegionPasses(split))
                    materialBins.Add(pass, bins);
        }
        TargetDrawingStep[] steps = graph.GetRegionPasses(first ? GraphData.TargetFirstReference : GraphData.TargetSecondReference)
            .Select(pass => new TargetDrawingStep(pass, materialBins.GetValueOrDefault(pass) ?? [])).ToArray();
        _targetDrawingVersion++;
        if (first) { _targetFirstRaster = raster; _targetFirstDrawing = steps; }
        else { _targetSecondRaster = raster; _targetSecondDrawing = steps; }
        return steps;
    }

    private void CollectTargetMaterialBins(ulong firstSource, ulong endSource, HashSet<uint> bins)
    {
        bins.Clear();
        int row = Array.BinarySearch(_targetSourceOffsets, firstSource);
        if (row < 0) row = ~row - 1;
        while (row < _targetSourceMaterialBins.Length && _targetSourceOffsets[row] < endSource)
        {
            if (_targetSourceOffsets[row + 1] > firstSource)
                bins.UnionWith(_targetSourceMaterialBins[row]);
            row++;
        }
    }

    private void BindTargetSourceRanges(ref RenderGraphEdit graph, GraphPassId passId)
    {
        PassDefinition pass = graph.GetPass(passId);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(GraphData.Resources.SourceClusterRanges, structureStride: 8)));
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.TargetSourceOffsets, structureStride: 8), PipelineSync.ComputeShading);
        _ = pass.Bind(GraphData.TargetBatchUniforms, PipelineSync.ComputeShading);
    }

    private void RecordTargetGeometry(ref RenderGraphFrame graph, RenderInstanceBatch instances, uint sourceViewCount)
    {
        graph.SetRegionEnabled(GraphData.TargetFirstReference, false);
        graph.SetRegionEnabled(GraphData.TargetSecondReference, false);
        ulong sourceTotal = Math.Max(1ul, _targetSourceOffsets[instances.InstanceCount]);
        uint targetViews = Math.Min(_renderContext.MaximumInvocationCount, checked(sourceViewCount * _renderContext.ViewDetailCount));
        TargetDrawingStep[] firstShape = GetTargetDrawingSteps(ref graph, true);
        TargetDrawingStep[] secondShape = GetTargetDrawingSteps(ref graph, false);
        bool sameBatches = _targetBatchSourceCount == sourceTotal && _targetBatchViewCount == targetViews &&
            _targetBatchDrawingVersion == _targetDrawingVersion &&
            _targetBatchMaterialVersion == _materialAssignments.Current.TopologyVersion && ReferenceEquals(_targetBatchInstances, instances);
        if (!sameBatches)
        {
            var batches = new List<KeyValuePair<uint, TargetSourceBatch>>();
            var bins = new HashSet<uint>();
            uint viewCount = Math.Min(targetViews, _options.MaxCandidates);
            uint index = 0;
            for (uint firstView = 0; firstView < targetViews; firstView += viewCount)
            {
                uint views = Math.Min(viewCount, targetViews - firstView);
                uint sourceCount = _options.MaxCandidates / views;
                if (sourceCount == 0u) throw new InvalidOperationException("A target view set exceeds the indirect raster count range.");
                for (ulong first = 0; first < sourceTotal; first += sourceCount)
                {
                    var values = new ClusterTargetBatchUniforms
                    {
                        FirstSourceCluster = first,
                        SourceClusterCount = checked((uint)Math.Min(sourceCount, sourceTotal - first)),
                        FirstTargetView = firstView, TargetViewCount = views,
                    };
                    CollectTargetMaterialBins(first, first + values.SourceClusterCount, bins);
                    uint[] materials = [.. bins.Order()];
                    batches.Add(new(index * 2, new(values, materials, _targetDrawingVersion)));
                    values.CollectDeferredOnly = 1;
                    batches.Add(new(index * 2 + 1, new(values, materials, _targetDrawingVersion)));
                    index++;
                }
            }
            _targetBatches = [.. batches];
            _targetBatchesChanged = true;
            _targetBatchSourceCount = sourceTotal;
            _targetBatchViewCount = targetViews;
            _targetBatchDrawingVersion = _targetDrawingVersion;
            _targetBatchMaterialVersion = _materialAssignments.Current.TopologyVersion;
            _targetBatchInstances = instances;
        }
        bool retained = !_targetBatchesChanged;
        graph.SetRegionEnabled(GraphData.TargetFirstDraws.Parent, retained);
        graph.SetRegionEnabled(GraphData.TargetSecondDraws.Parent, retained);
        if (retained) return;
        int deferredStepCount = Array.FindIndex(firstShape, step => step.Pass == GraphData.TargetFirstCull) + 1;
        foreach (var pair in _targetBatches)
        {
            ClusterTargetBatchUniforms data = pair.Value.Values;
            bool deferred = data.CollectDeferredOnly != 0u;
            GraphRegionId region = deferred ? GraphData.TargetSecondBatches : GraphData.TargetFirstBatches;
            GraphBufferSlice values = graph.Upload(in data, BufferUsages.Constant, "Cluster target source batch");
            var bins = new HashSet<uint>(pair.Value.MaterialBins);
            ReadOnlySpan<TargetDrawingStep> discovery = deferred ? firstShape.AsSpan(0, deferredStepCount) : firstShape;
            foreach (TargetDrawingStep step in discovery) AddCopy(ref graph, step);
            if (deferred)
                foreach (TargetDrawingStep step in secondShape) AddCopy(ref graph, step);

            void AddCopy(ref RenderGraphFrame frame, TargetDrawingStep step)
            {
                if (step.MaterialBins.Length != 0 && !bins.Overlaps(step.MaterialBins)) return;
                frame.AddPassCopy(step.Pass, "Cluster target source drawing", region, GraphData.TargetBatchUniforms, values);
            }
        }
    }

    private void ReconcileTargetBatches()
    {
        if (!_renderContext.IsValid || !_targetBatchesChanged) return;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            var comparer = new TargetSourceBatchComparer();
            var desired = _targetBatches.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            foreach (var pair in GraphData.TargetBatches)
                if (!desired.TryGetValue(pair.Key, out TargetSourceBatch current) || !comparer.Equals(pair.Value, current))
                {
                    RemoveParameters(GraphData.TargetBatchParameters[pair.Key]);
                    GraphData.TargetBatchParameters = GraphData.TargetBatchParameters.Remove(pair.Key);
                }
            GraphData.TargetFirstDraws = GraphData.TargetFirstDraws.Reconcile(ref edit,
                _targetBatches.Where(static pair => (pair.Key & 1u) == 0), BuildTargetBatch);
            GraphData.TargetSecondDraws = GraphData.TargetSecondDraws.Reconcile(ref edit,
                _targetBatches.Where(static pair => (pair.Key & 1u) != 0), BuildTargetBatch);
            GraphData.TargetBatches = _targetBatches;
        });
        _targetBatchesChanged = false;
    }

    private void BuildTargetBatch(ref RenderGraphEdit edit, GraphRegionId region, uint key, TargetSourceBatch batch)
    {
        ClusterTargetBatchUniforms values = batch.Values;
        GraphBufferInput parameters = CreateParameters(ref edit,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref values, 1)), "Cluster target source batch");
        GraphData.TargetBatchParameters = GraphData.TargetBatchParameters.Add(key, parameters);
        var bins = new HashSet<uint>(batch.MaterialBins);
        bool deferred = values.CollectDeferredOnly != 0;
        int count = deferred ? Array.FindIndex(_targetFirstDrawing, step => step.Pass == GraphData.TargetFirstCull) + 1 : _targetFirstDrawing.Length;
        foreach (TargetDrawingStep step in _targetFirstDrawing.AsSpan(0, count)) Add(ref edit, step);
        if (deferred)
            foreach (TargetDrawingStep step in _targetSecondDrawing) Add(ref edit, step);

        void Add(ref RenderGraphEdit builder, TargetDrawingStep step)
        {
            if (step.MaterialBins.Length != 0 && !bins.Overlaps(step.MaterialBins)) return;
            builder.AddPassCopy(step.Pass, "Cluster target source drawing", region, GraphData.TargetBatchUniforms, parameters);
        }
    }
}

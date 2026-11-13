using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;
/// <summary>
/// Owns the complete executable pipeline set selected by one Cluster render asset. Source paths
/// and register numbers never enter frame execution; all descriptors are linked from cooked
/// entry-point reflection.
/// </summary>
internal sealed class ClusterPipelineSet : IDisposable
{
    private static readonly int RequiredPipelineCount = Enum.GetValues<ClusterShaderOperationRole>().Count(static role => role != ClusterShaderOperationRole.None);
    private readonly List<ClusterPipeline> _pipelines = [];
    internal IReadOnlyList<ClusterPipeline> Pipelines => _pipelines;
    internal IEnumerable<(bool TargetFilters, bool MultipleViews, bool LayerTiles)> TraversalModes => _traversals.Keys;
    internal Dictionary<global::SomeEngine.Graphics.Pipeline, ClusterPipeline> Replacements(ClusterPipelineSet next)
    {
        foreach (var mode in TraversalModes)
            next.GetTraversal(mode.TargetFilters, mode.MultipleViews, mode.LayerTiles);
        var result = new Dictionary<global::SomeEngine.Graphics.Pipeline, ClusterPipeline>();
        for (int index = 0; index < _pipelines.Count; index++)
            if (!ReferenceEquals(_pipelines[index].Pipeline, next._pipelines[index].Pipeline))
                result[_pipelines[index].Pipeline] = next._pipelines[index];
        return result;
    }
    private readonly RenderPipelines _renderPipelines;
    private readonly Dictionary<(bool TargetFilters, bool MultipleViews, bool LayerTiles), ClusterComputePipeline> _traversals = [];
    private readonly Func<bool, bool, bool, ClusterComputePipeline> _createTraversal;
    private bool _disposed;
    internal ClusterPipelineSet(IGraphicsBackend backend, Device device, RenderPipelines pipelines, ClusterShaders configuration, ClusterVisibilityMode visibilityMode)
    {
        _renderPipelines = pipelines;
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);
        _pipelines.EnsureCapacity(RequiredPipelineCount);
        try
        {
            Dictionary<ClusterShaderOperationRole, ShaderOperation> operations = IndexOperations(configuration);
            ClusterComputePipeline Compute(ClusterShaderOperationRole role, string name, string? entryPoint = null, string[]? specializations = null) => CreateCompute(backend, device, operations, _pipelines, role, name, entryPoint, specializations);
            ClusterRasterPipeline Raster(ClusterShaderOperationRole role, ReadOnlySpan<Format> colorFormats, Format? depthStencilFormat = null, RasterizerState rasterizerState = default, DepthStencilState depthStencilState = default, ReadOnlySpan<BlendAttachmentState> blendAttachments = default, uint sampleCount = 1, string? name = null, uint sampleMask = uint.MaxValue, bool alphaToCoverage = false, string? vertexEntry = null, string? pixelEntry = null) => CreateRaster(backend, device, operations, _pipelines, role, colorFormats, depthStencilFormat, rasterizerState, depthStencilState, blendAttachments, sampleCount, name, sampleMask, alphaToCoverage, vertexEntry, pixelEntry);
            _createTraversal = (targetFilters, multipleViews, layerTiles) => Compute(ClusterShaderOperationRole.BvhTraversal, $"Cluster BVH traversal (target filters: {targetFilters}, multiple views: {multipleViews}, layer tiles: {layerTiles})", specializations: [targetFilters ? "true" : "false", multipleViews ? "true" : "false", layerTiles ? "true" : "false"]);
            LeafFlagFixups = Compute(ClusterShaderOperationRole.LeafFlagFixups, "Cluster streaming leaf flags");
            PageRequests = Compute(ClusterShaderOperationRole.PageRequests, "Cluster page request priorities");
            SceneBounds = Compute(ClusterShaderOperationRole.SceneBounds, "Cluster spatial scene bounds");
            SceneBoundsCommit = Compute(ClusterShaderOperationRole.SceneBounds, "Commit Cluster instance bounds", entryPoint: "CommitClusterSceneBounds");
            TraversalInitialize = Compute(ClusterShaderOperationRole.BvhTraversalInitialize, "Cluster screen hierarchy roots", specializations: ["false"]);
            TargetTraversalInitialize = Compute(ClusterShaderOperationRole.BvhTraversalInitialize, "Cluster shadow hierarchy roots", specializations: ["true"]);
            DeferredResume = Compute(ClusterShaderOperationRole.BvhDeferredResume, "Cluster deferred hierarchy resume");
            CullReset = Compute(ClusterShaderOperationRole.CullReset, "Cluster cull reset");
            CullPhase1 = Compute(ClusterShaderOperationRole.CullPhaseOne, "Cluster cull phase one");
            CullPhase2 = Compute(ClusterShaderOperationRole.CullPhaseTwo, "Cluster cull phase two");
            RasterBinReset = Compute(ClusterShaderOperationRole.RasterBinningReset, "Cluster raster bins reset");
            RasterBinCount = Compute(ClusterShaderOperationRole.RasterBinningCount, "Cluster raster bins count");
            RasterBinScanShader = GetEntry(GetOperation(operations, ClusterShaderOperationRole.RasterBinningReserve),
                ClusterShaderOperationRole.RasterBinningReserve, SlangStage.Compute).Shader;
            RasterBinPrepare = Compute(ClusterShaderOperationRole.RasterBinningReserve, "Cluster raster bin counts", "PrepareRasterBinOffsets");
            RasterBinReserve = Compute(ClusterShaderOperationRole.RasterBinningReserve, "Cluster raster bins reserve");
            RasterSpillPrepare = Compute(ClusterShaderOperationRole.RasterBinningReserve, "Cluster spilled raster arguments", "PrepareRasterSpillArguments");
            RasterBinScatter = Compute(ClusterShaderOperationRole.RasterBinningScatter, "Cluster raster bins scatter");
            TessellationBinReset = Compute(ClusterShaderOperationRole.TessellationBinningReset, "Cluster tessellation bins reset");
            TessellationBinCount = Compute(ClusterShaderOperationRole.TessellationBinningCount, "Cluster tessellation bins count");
            TessellationBinReserve = Compute(ClusterShaderOperationRole.TessellationBinningReserve, "Cluster tessellation bins reserve");
            TessellationBinScatter = Compute(ClusterShaderOperationRole.TessellationBinningScatter, "Cluster tessellation bins scatter");
            WinnerClear = visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : Compute(visibilityMode == ClusterVisibilityMode.ImageAtomic64 ? ClusterShaderOperationRole.VisibilityClearImage : ClusterShaderOperationRole.VisibilityClearBuffer,
                "Cluster visibility clear", entryPoint: visibilityMode == ClusterVisibilityMode.NonAtomic ? "ClearVisibilityNonAtomic" : null);
            WinnerResolve = visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : Raster(visibilityMode == ClusterVisibilityMode.ImageAtomic64 ? ClusterShaderOperationRole.VisibilityResolveImage : ClusterShaderOperationRole.VisibilityResolveBuffer,
                [Format.R32UInt], depthStencilFormat: Format.D32Float, rasterizerState: new RasterizerState(Cull: CullType.None),
                depthStencilState: new DepthStencilState(DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.Always),
                name: "Cluster visibility resolve", pixelEntry: visibilityMode == ClusterVisibilityMode.NonAtomic ? "PSResolveVisibilityNonAtomic" : null);
            HiZFirst = Compute(ClusterShaderOperationRole.HiZInitialize, "Cluster HiZ initialize");
            HiZDownsample = Compute(ClusterShaderOperationRole.HiZReduce, "Cluster HiZ reduce");
            HiZDownsampleTwo = Compute(ClusterShaderOperationRole.HiZReducePair, "Cluster HiZ double reduce");
            ShadeBinClearPrepare = Compute(ClusterShaderOperationRole.MaterialBinningReset, "Cluster material bin clear");
            ShadeBinCount = Compute(ClusterShaderOperationRole.MaterialBinningCount, "Cluster material bin count");
            ShadeBinReserve = Compute(ClusterShaderOperationRole.MaterialBinningReserve, "Cluster material bin reserve");
            ShadeBinScatter = Compute(ClusterShaderOperationRole.MaterialBinningScatter, "Cluster material bin scatter");
            Resolve = Compute(ClusterShaderOperationRole.VisibilityResolve, "Cluster visibility resolve");
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            DisposeOwnedResources(ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException("Cluster pipeline-set construction failed and cleanup also reported failures.", cleanupFailures);
            }

            throw;
        }
    }

    internal ClusterComputePipeline GetTraversal(bool targetFilters, bool multipleViews, bool layerTiles = false)
    {
        var capabilities = (targetFilters, multipleViews, layerTiles);
        if (!_traversals.TryGetValue(capabilities, out ClusterComputePipeline? pipeline))
        {
            pipeline = _createTraversal(targetFilters, multipleViews, layerTiles);
            _traversals.Add(capabilities, pipeline);
        }

        return pipeline;
    }

    internal ClusterComputePipeline PageRequests { get; }
    internal ClusterComputePipeline LeafFlagFixups { get; }
    internal ClusterComputePipeline SceneBounds { get; }
    internal ClusterComputePipeline SceneBoundsCommit { get; }
    internal ClusterComputePipeline TraversalInitialize { get; }
    internal ClusterComputePipeline TargetTraversalInitialize { get; }
    internal ClusterComputePipeline DeferredResume { get; }
    internal ClusterComputePipeline CullReset { get; }
    internal ClusterComputePipeline CullPhase1 { get; }
    internal ClusterComputePipeline CullPhase2 { get; }
    internal ClusterComputePipeline RasterBinReset { get; }
    internal ClusterComputePipeline RasterBinCount { get; }
    internal Shader RasterBinScanShader { get; }
    internal ClusterComputePipeline RasterBinPrepare { get; }
    internal ClusterComputePipeline RasterBinReserve { get; }
    internal ClusterComputePipeline RasterSpillPrepare { get; }
    internal ClusterComputePipeline RasterBinScatter { get; }
    internal ClusterComputePipeline TessellationBinReset { get; }
    internal ClusterComputePipeline TessellationBinCount { get; }
    internal ClusterComputePipeline TessellationBinReserve { get; }
    internal ClusterComputePipeline TessellationBinScatter { get; }
    internal ClusterComputePipeline? WinnerClear { get; }
    internal ClusterRasterPipeline? WinnerResolve { get; }
    internal ClusterComputePipeline HiZFirst { get; }
    internal ClusterComputePipeline HiZDownsample { get; }
    internal ClusterComputePipeline HiZDownsampleTwo { get; }
    internal ClusterComputePipeline ShadeBinClearPrepare { get; }
    internal ClusterComputePipeline ShadeBinCount { get; }
    internal ClusterComputePipeline ShadeBinReserve { get; }
    internal ClusterComputePipeline ShadeBinScatter { get; }
    internal ClusterComputePipeline Resolve { get; }

    private ClusterComputePipeline CreateCompute(IGraphicsBackend backend, Device device, IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations, List<ClusterPipeline> ownedPipelines, ClusterShaderOperationRole role, string name, string? entryPoint = null, string[]? specializations = null)
    {
        ShaderOperation operation = GetOperation(operations, role);
        (Shader Shader, EntryPointReflection Entry) compute = GetEntry(operation, role, SlangStage.Compute);
        string selectedEntry = entryPoint ?? compute.Entry.Name;
        if (specializations is not null && compute.Shader.HasSpecializations(selectedEntry))
        {
            Shader shader = compute.Shader.RequireSpecialization(selectedEntry, specializations);
            compute = (shader, shader.RequireSlangEntry(shader.RequireSlangTarget(device.Capabilities.CompileTarget), selectedEntry, SlangStage.Compute));
        }

        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterComputePipeline? created = null;
        try
        {
            created = entryPoint is null ? ClusterComputePipeline.Create(_renderPipelines, compute.Shader, compute.Entry, name) : ClusterComputePipeline.Create(_renderPipelines, compute.Shader, entryPoint, name);
            ownedPipelines.Add(created);
            ClusterComputePipeline result = created;
            created = null;
            return result;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (created is not null)
                TryDispose(created, ref cleanupFailures);
            ThrowWithCleanupFailures(primary, cleanupFailures, $"Cluster compute pipeline '{role}' construction failed.");
            throw;
        }
    }

    private ClusterRasterPipeline CreateRaster(IGraphicsBackend backend, Device device, IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations, List<ClusterPipeline> ownedPipelines, ClusterShaderOperationRole role, ReadOnlySpan<Format> colorFormats, Format? depthStencilFormat, RasterizerState rasterizerState, DepthStencilState depthStencilState, ReadOnlySpan<BlendAttachmentState> blendAttachments, uint sampleCount, string? name, uint sampleMask, bool alphaToCoverage, string? vertexEntry = null, string? pixelEntry = null)
    {
        ShaderOperation operation = GetOperation(operations, role);
        (Shader Shader, EntryPointReflection Entry) vertex = GetEntry(operation, role, SlangStage.Vertex);
        (Shader Shader, EntryPointReflection Entry) pixel = GetEntry(operation, role, SlangStage.Fragment);
        Shader vertexShader = vertex.Shader;
        Shader pixelShader = pixel.Shader;
        if (!ReferenceEquals(vertexShader, pixelShader))
            throw new InvalidDataException($"Cluster shader operation '{role}' spans shader assets.");
        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterRasterPipeline? created = null;
        try
        {
            Shader shader = vertex.Shader;
            bool usesMesh = role == ClusterShaderOperationRole.HardwareVisibilityRaster && ClusterRasterPipeline.SupportsMesh(backend, device);
            string geometryEntry = vertexEntry ?? vertex.Entry.Name;
            if (usesMesh)
                geometryEntry = ClusterRasterPipeline.SupportsMeshlets(backend, device) && ClusterMaterialEntries.HasEntry(shader, device.Capabilities.CompileTarget, "MSVisBufferMeshlet", SlangStage.Mesh) ? "MSVisBufferMeshlet" : "MSVisBuffer";
            if (role == ClusterShaderOperationRole.HardwareVisibilityRaster)
            {
                EntryPointReflection rasterPixel = shader.RequireSlangEntry(shader.RequireSlangTarget(device.Capabilities.CompileTarget), pixelEntry ?? pixel.Entry.Name, SlangStage.Fragment);
                rasterizerState = rasterizerState with
                {
                    Cull = ClusterGeometryRequirements.UsesBothRasterFaces(rasterPixel) ? CullType.None : CullType.Back
                };
            }

            created = ClusterRasterPipeline.Create(_renderPipelines, shader, geometryEntry, pixelEntry ?? pixel.Entry.Name, colorFormats, depthStencilFormat, rasterizerState, depthStencilState, blendAttachments, sampleCount, name, sampleMask, alphaToCoverage, geometryStage: usesMesh ? SlangStage.Mesh : SlangStage.Vertex);
            ownedPipelines.Add(created);
            ClusterRasterPipeline result = created;
            created = null;
            return result;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (created is not null)
                TryDispose(created, ref cleanupFailures);
            ThrowWithCleanupFailures(primary, cleanupFailures, $"Cluster raster pipeline '{role}' construction failed.");
            throw;
        }
    }

    private static ShaderOperation GetOperation(IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations, ClusterShaderOperationRole role) => operations.TryGetValue(role, out ShaderOperation? operation) ? operation : throw new InvalidDataException($"Cluster render asset has no '{role}' shader operation.");
    private static (Shader Shader, EntryPointReflection Entry) GetEntry(ShaderOperation operation, ClusterShaderOperationRole role, SlangStage stage)
    {
        if (operation.Shaders.Count != operation.Entries.Count)
            throw new InvalidDataException($"Cluster shader operation '{role}' has mismatched shader data.");
        int resultIndex = -1;
        for (int index = 0; index < operation.Entries.Count; index++)
        {
            if (operation.Entries[index].Stage != stage)
                continue;
            if (resultIndex >= 0)
            {
                throw new InvalidDataException($"Cluster shader operation '{role}' repeats the {stage} stage.");
            }

            resultIndex = index;
        }

        if (resultIndex < 0)
        {
            throw new InvalidDataException($"Cluster shader operation '{role}' has no {stage} shader entry.");
        }

        return (operation.Shaders[resultIndex], operation.Entries[resultIndex]);
    }

    private static Dictionary<ClusterShaderOperationRole, ShaderOperation> IndexOperations(ClusterShaders configuration)
    {
        IList<ShaderOperation> operations = configuration.Operations ?? throw new InvalidDataException("Cluster render asset has no shader operations.");
        if (operations.Count != RequiredPipelineCount)
        {
            throw new InvalidDataException($"Cluster render asset must define exactly {RequiredPipelineCount} shader operations; " + $"it defines {operations.Count}.");
        }

        var result = new Dictionary<ClusterShaderOperationRole, ShaderOperation>(operations.Count);
        foreach (ShaderOperation operation in operations)
        {
            if (operation is null || operation.Role == ClusterShaderOperationRole.None || !Enum.IsDefined(operation.Role))
            {
                throw new InvalidDataException("Cluster render asset contains an invalid shader-operation role.");
            }

            bool isRaster = IsRasterOperation(operation.Role);
            IList<Shader> shaders = operation.Shaders;
            IList<EntryPointReflection> entries = operation.Entries;
            bool validShape = shaders.Count == entries.Count && (isRaster ? entries.Count == 2 && entries.Count(static entry => entry.Stage == SlangStage.Vertex) == 1 && entries.Count(static entry => entry.Stage == SlangStage.Fragment) == 1 : entries.Count == 1 && entries[0].Stage == SlangStage.Compute);
            if (!validShape)
            {
                throw new InvalidDataException($"Cluster shader operation '{operation.Role}' has an invalid shader-stage set.");
            }

            Shader? selectedShader = null;
            for (int index = 0; index < shaders.Count; index++)
            {
                Shader current = shaders[index];
                EntryPointReflection entry = entries[index];
                if (entry == EntryPointReflection.Null || string.IsNullOrWhiteSpace(entry.Name))
                {
                    throw new InvalidDataException($"Cluster shader operation '{operation.Role}' has an empty entry point.");
                }

                if (selectedShader is not null && !ReferenceEquals(selectedShader, current))
                {
                    throw new InvalidDataException($"Cluster shader operation '{operation.Role}' spans shader assets.");
                }

                selectedShader = current;
            }

            if (!result.TryAdd(operation.Role, operation))
            {
                throw new InvalidDataException($"Cluster render asset repeats shader operation '{operation.Role}'.");
            }
        }

        return result;
    }

    private static bool IsRasterOperation(ClusterShaderOperationRole role) => role is ClusterShaderOperationRole.HardwareVisibilityRaster or ClusterShaderOperationRole.VisibilityResolveImage or ClusterShaderOperationRole.VisibilityResolveBuffer;
    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        DisposeOwnedResources(ref failures);
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void DisposeOwnedResources(ref List<Exception>? failures)
    {
        for (int index = _pipelines.Count - 1; index >= 0; index--)
            TryDispose(_pipelines[index], ref failures);
        _pipelines.Clear();
    }

    private static void ThrowWithCleanupFailures(Exception primary, List<Exception>? cleanupFailures, string message)
    {
        if (cleanupFailures is null)
            return;
        cleanupFailures.Insert(0, primary);
        throw new AggregateException(message, cleanupFailures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try
        {
            value.Dispose();
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
    }
}

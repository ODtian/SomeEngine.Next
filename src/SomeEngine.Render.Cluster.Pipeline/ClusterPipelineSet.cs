using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the complete executable pipeline set selected by one Cluster render asset. Source paths
/// and register numbers never enter frame execution; all descriptors are linked from cooked
/// entry-point reflection.
/// </summary>
internal sealed class ClusterPipelineSet : IDisposable
{
    private const int RequiredPipelineCount =
        (int)ClusterShaderOperationRole.ToneMapAndPresent;

    private readonly List<ClusterPipeline> _pipelines = [];
    private bool _disposed;

    internal ClusterPipelineSet(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        AssetHandle<ClusterShaders> configuration,
        Format outputFormat)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assets);
        if (!configuration.IsValid || configuration.LoadState != AssetLoadState.Ready)
            throw new ArgumentException("The Cluster render asset must be ready.", nameof(configuration));
        _pipelines.EnsureCapacity(RequiredPipelineCount);
        AssetRead<ClusterShaders> configurationRead = assets.Read(configuration);
        try
        {
            using (configurationRead)
            {
                Dictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations =
                    IndexOperations(configurationRead.Value);

                ClusterComputePipeline Compute(ClusterShaderOperationRole role, string name) =>
                    CreateCompute(backend, device, assets, operations, _pipelines, role, name);

                ClusterRasterPipeline Raster(
                    ClusterShaderOperationRole role,
                    ReadOnlySpan<Format> colorFormats,
                    Format? depthStencilFormat = null,
                    RasterizerState rasterizerState = default,
                    DepthStencilState depthStencilState = default,
                    ReadOnlySpan<BlendAttachmentState> blendAttachments = default,
                    uint sampleCount = 1,
                    string? name = null,
                    uint sampleMask = uint.MaxValue,
                    bool alphaToCoverage = false) =>
                    CreateRaster(
                        backend,
                        device,
                        assets,
                        operations,
                        _pipelines,
                        role,
                        colorFormats,
                        depthStencilFormat,
                        rasterizerState,
                        depthStencilState,
                        blendAttachments,
                        sampleCount,
                        name,
                        sampleMask,
                        alphaToCoverage);

                Traversal = Compute(ClusterShaderOperationRole.BvhTraversal, "Cluster BVH traversal");
                CullClearPhase1 = Compute(ClusterShaderOperationRole.CullPhaseOneReset, "Cluster cull phase-one clear");
                CullPhase1 = Compute(ClusterShaderOperationRole.CullPhaseOne, "Cluster cull phase one");
                CullClearPhase2 = Compute(ClusterShaderOperationRole.CullPhaseTwoReset, "Cluster cull phase-two clear");
                CullPhase2 = Compute(ClusterShaderOperationRole.CullPhaseTwo, "Cluster cull phase two");

                RasterDeformBinReset = Compute(
                    ClusterShaderOperationRole.RasterDeformBinningReset,
                    "Cluster raster/deform bins reset");
                RasterDeformBinCount = Compute(
                    ClusterShaderOperationRole.RasterDeformBinningCount,
                    "Cluster raster/deform bins count");
                RasterDeformBinReserve = Compute(
                    ClusterShaderOperationRole.RasterDeformBinningReserve,
                    "Cluster raster/deform bins reserve");
                RasterDeformBinScatter = Compute(
                    ClusterShaderOperationRole.RasterDeformBinningScatter,
                    "Cluster raster/deform bins scatter");
                DeformCachePopulate = Compute(
                    ClusterShaderOperationRole.DeformCachePopulate,
                    "Cluster deform cache populate");
                SoftwareRaster = Compute(
                    ClusterShaderOperationRole.SoftwareVisibilityRaster,
                    "Cluster software visibility raster");

                HardwareRaster = Raster(
                    ClusterShaderOperationRole.HardwareVisibilityRaster,
                    [Format.R32UInt],
                    depthStencilFormat: Format.D32Float,
                    rasterizerState: new RasterizerState(Cull: CullType.Back),
                    depthStencilState: new DepthStencilState(
                        DepthTest: true,
                        DepthWrite: true,
                        DepthComparison: CompareOperation.LessOrEqual),
                    name: "Cluster hardware visibility raster");
                DepthMerge = Raster(
                    ClusterShaderOperationRole.SoftwareDepthMerge,
                    [],
                    depthStencilFormat: Format.D32Float,
                    rasterizerState: new RasterizerState(Cull: CullType.None),
                    depthStencilState: new DepthStencilState(
                        DepthTest: true,
                        DepthWrite: true,
                        DepthComparison: CompareOperation.Less),
                    name: "Cluster software depth merge");

                HiZFirst = Compute(ClusterShaderOperationRole.HiZInitialize, "Cluster HiZ initialize");
                HiZDownsample = Compute(ClusterShaderOperationRole.HiZReduce, "Cluster HiZ reduce");
                HiZDownsampleTwo = Compute(ClusterShaderOperationRole.HiZReducePair, "Cluster HiZ double reduce");
                ShadeBinClearPrepare = Compute(
                    ClusterShaderOperationRole.MaterialBinningReset,
                    "Cluster material bin clear");
                ShadeBinCount = Compute(
                    ClusterShaderOperationRole.MaterialBinningCount,
                    "Cluster material bin count");
                ShadeBinReserve = Compute(
                    ClusterShaderOperationRole.MaterialBinningReserve,
                    "Cluster material bin reserve");
                ShadeBinScatter = Compute(
                    ClusterShaderOperationRole.MaterialBinningScatter,
                    "Cluster material bin scatter");
                MotionVectors = Compute(ClusterShaderOperationRole.MotionVectors, "Cluster motion vectors");
                Resolve = Compute(ClusterShaderOperationRole.VisibilityResolve, "Cluster visibility resolve");

                TemporalResolve = Raster(
                    ClusterShaderOperationRole.TemporalResolve,
                    [Format.R16G16B16A16Float],
                    rasterizerState: new RasterizerState(Cull: CullType.None),
                    name: "Cluster temporal resolve");
                Tonemap = Raster(
                    ClusterShaderOperationRole.ToneMapAndPresent,
                    [outputFormat],
                    rasterizerState: new RasterizerState(Cull: CullType.None),
                    name: "Cluster tone map and present");
            }
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            DisposeOwnedResources(ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster pipeline-set construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    internal ClusterComputePipeline Traversal { get; }
    internal ClusterComputePipeline CullClearPhase1 { get; }
    internal ClusterComputePipeline CullPhase1 { get; }
    internal ClusterComputePipeline CullClearPhase2 { get; }
    internal ClusterComputePipeline CullPhase2 { get; }
    internal ClusterComputePipeline RasterDeformBinReset { get; }
    internal ClusterComputePipeline RasterDeformBinCount { get; }
    internal ClusterComputePipeline RasterDeformBinReserve { get; }
    internal ClusterComputePipeline RasterDeformBinScatter { get; }
    internal ClusterComputePipeline DeformCachePopulate { get; }
    internal ClusterComputePipeline SoftwareRaster { get; }
    internal ClusterRasterPipeline HardwareRaster { get; }
    internal ClusterRasterPipeline DepthMerge { get; }
    internal ClusterComputePipeline HiZFirst { get; }
    internal ClusterComputePipeline HiZDownsample { get; }
    internal ClusterComputePipeline HiZDownsampleTwo { get; }
    internal ClusterComputePipeline ShadeBinClearPrepare { get; }
    internal ClusterComputePipeline ShadeBinCount { get; }
    internal ClusterComputePipeline ShadeBinReserve { get; }
    internal ClusterComputePipeline ShadeBinScatter { get; }
    internal ClusterComputePipeline MotionVectors { get; }
    internal ClusterComputePipeline Resolve { get; }
    internal ClusterRasterPipeline TemporalResolve { get; }
    internal ClusterRasterPipeline Tonemap { get; }

    private static ClusterComputePipeline CreateCompute(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        IReadOnlyDictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations,
        List<ClusterPipeline> ownedPipelines,
        ClusterShaderOperationRole role,
        string name)
    {
        ClusterShaderOperation operation = GetOperation(operations, role);
        if (string.IsNullOrWhiteSpace(operation.ComputeEntryPoint)
            || !string.IsNullOrWhiteSpace(operation.VertexEntryPoint)
            || !string.IsNullOrWhiteSpace(operation.PixelEntryPoint))
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' is not a compute operation.");
        }

        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterComputePipeline? created = null;
        try
        {
            using (AssetRead<Shader> shaderRead = LoadShaderRead(assets, operation.Shader, role))
            {
                created = ClusterComputePipeline.Create(
                    backend,
                    device,
                    shaderRead.Value,
                    operation.ComputeEntryPoint,
                    name);
            }
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
            ThrowWithCleanupFailures(
                primary,
                cleanupFailures,
                $"Cluster compute pipeline '{role}' construction failed.");
            throw;
        }
    }

    private static ClusterRasterPipeline CreateRaster(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        IReadOnlyDictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations,
        List<ClusterPipeline> ownedPipelines,
        ClusterShaderOperationRole role,
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat,
        RasterizerState rasterizerState,
        DepthStencilState depthStencilState,
        ReadOnlySpan<BlendAttachmentState> blendAttachments,
        uint sampleCount,
        string? name,
        uint sampleMask,
        bool alphaToCoverage)
    {
        ClusterShaderOperation operation = GetOperation(operations, role);
        if (!string.IsNullOrWhiteSpace(operation.ComputeEntryPoint)
            || string.IsNullOrWhiteSpace(operation.VertexEntryPoint)
            || string.IsNullOrWhiteSpace(operation.PixelEntryPoint))
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' is not a raster operation.");
        }

        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterRasterPipeline? created = null;
        try
        {
            using (AssetRead<Shader> shaderRead = LoadShaderRead(assets, operation.Shader, role))
            {
                created = ClusterRasterPipeline.Create(
                    backend,
                    device,
                    shaderRead.Value,
                    operation.VertexEntryPoint,
                    operation.PixelEntryPoint,
                    colorFormats,
                    depthStencilFormat,
                    rasterizerState,
                    depthStencilState,
                    blendAttachments,
                    sampleCount,
                    name,
                    sampleMask,
                    alphaToCoverage);
            }
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
            ThrowWithCleanupFailures(
                primary,
                cleanupFailures,
                $"Cluster raster pipeline '{role}' construction failed.");
            throw;
        }
    }

    private static ClusterShaderOperation GetOperation(
        IReadOnlyDictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations,
        ClusterShaderOperationRole role)
        => operations.TryGetValue(role, out ClusterShaderOperation? operation)
            ? operation
            : throw new InvalidDataException(
                $"Cluster render asset has no '{role}' shader operation.");

    private static AssetRead<Shader> LoadShaderRead(
        AssetLoader assets,
        ShaderAssetRef? reference,
        ClusterShaderOperationRole role)
    {
        if (!AssetGuid.TryParse(reference?.ShaderGuid, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Cluster shader operation '{role}' has no shader.");
        AssetHandle<Shader> handle = assets.Load(new AssetId<Shader>(guid));
        if (handle.LoadState != AssetLoadState.Ready)
            assets.WaitAsync(handle).AsTask().GetAwaiter().GetResult();
        return assets.Read(handle);
    }

    private static Dictionary<ClusterShaderOperationRole, ClusterShaderOperation> IndexOperations(
        ClusterShaders configuration)
    {
        IList<ClusterShaderOperation> operations = configuration.Operations
            ?? throw new InvalidDataException("Cluster render asset has no shader operations.");
        if (operations.Count != RequiredPipelineCount)
        {
            throw new InvalidDataException(
                $"Cluster render asset must define exactly {RequiredPipelineCount} shader operations; " +
                $"it defines {operations.Count}.");
        }
        var result = new Dictionary<ClusterShaderOperationRole, ClusterShaderOperation>(
            operations.Count);
        foreach (ClusterShaderOperation operation in operations)
        {
            if (operation is null ||
                operation.Role == ClusterShaderOperationRole.None ||
                !Enum.IsDefined(operation.Role))
            {
                throw new InvalidDataException(
                    "Cluster render asset contains an invalid shader-operation role.");
            }
            if (!AssetGuid.TryParse(operation.Shader?.ShaderGuid, out AssetGuid shaderGuid) ||
                shaderGuid.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Cluster shader operation '{operation.Role}' has no shader.");
            }

            bool isRaster = IsRasterOperation(operation.Role);
            bool validShape = isRaster
                ? string.IsNullOrWhiteSpace(operation.ComputeEntryPoint) &&
                    !string.IsNullOrWhiteSpace(operation.VertexEntryPoint) &&
                    !string.IsNullOrWhiteSpace(operation.PixelEntryPoint)
                : !string.IsNullOrWhiteSpace(operation.ComputeEntryPoint) &&
                    string.IsNullOrWhiteSpace(operation.VertexEntryPoint) &&
                    string.IsNullOrWhiteSpace(operation.PixelEntryPoint);
            if (!validShape)
            {
                throw new InvalidDataException(
                    $"Cluster shader operation '{operation.Role}' has an invalid entry-point shape.");
            }
            if (!result.TryAdd(operation.Role, operation))
            {
                throw new InvalidDataException(
                    $"Cluster render asset repeats shader operation '{operation.Role}'.");
            }
        }
        return result;
    }

    private static bool IsRasterOperation(ClusterShaderOperationRole role) => role is
        ClusterShaderOperationRole.HardwareVisibilityRaster or
        ClusterShaderOperationRole.SoftwareDepthMerge or
        ClusterShaderOperationRole.TemporalResolve or
        ClusterShaderOperationRole.ToneMapAndPresent;

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

    private static void ThrowWithCleanupFailures(
        Exception primary,
        List<Exception>? cleanupFailures,
        string message)
    {
        if (cleanupFailures is null)
            return;
        cleanupFailures.Insert(0, primary);
        throw new AggregateException(message, cleanupFailures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

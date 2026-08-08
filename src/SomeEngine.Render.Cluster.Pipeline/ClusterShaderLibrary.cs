using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the complete cooked shader/pipeline set selected by one Cluster render asset.  Source
/// paths and register numbers never enter the runtime; all descriptors are linked from cooked
/// entry-point reflection.
/// </summary>
internal sealed class ClusterShaderLibrary : IDisposable
{
    private readonly List<IDisposable> _reads = [];
    private readonly List<IDisposable> _pipelines = [];
    private readonly Dictionary<AssetGuid, Shader> _shaderCache = [];
    private readonly Dictionary<ClusterShaderOperationRole, ClusterShaderOperation> _operations;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly AssetLoader _assets;
    private readonly AssetRead<ClusterShaders> _configurationRead;
    private bool _disposed;

    internal ClusterShaderLibrary(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        AssetHandle<ClusterShaders> configuration,
        Format outputFormat)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        if (!configuration.IsValid || configuration.LoadState != AssetLoadState.Ready)
            throw new ArgumentException("The Cluster render asset must be ready.", nameof(configuration));
        _configurationRead = assets.Read(configuration);
        _reads.Add(_configurationRead);
        _operations = IndexOperations(_configurationRead.Value);

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
            DepthStencilFormat: Format.D32Float,
                RasterizerState: new RasterizerState(Cull: CullType.Back),
                DepthStencilState: new DepthStencilState(
                    DepthTest: true,
                    DepthWrite: true,
                    DepthComparison: CompareOperation.LessOrEqual),
            Name: "Cluster hardware visibility raster");
        DepthMerge = Raster(
            ClusterShaderOperationRole.SoftwareDepthMerge,
            [],
            DepthStencilFormat: Format.D32Float,
                RasterizerState: new RasterizerState(Cull: CullType.None),
                DepthStencilState: new DepthStencilState(
                    DepthTest: true,
                    DepthWrite: true,
                    DepthComparison: CompareOperation.Less),
            Name: "Cluster software depth merge");

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
            RasterizerState: new RasterizerState(Cull: CullType.None),
            Name: "Cluster temporal resolve");
        Tonemap = Raster(
            ClusterShaderOperationRole.ToneMapAndPresent,
            [outputFormat],
            RasterizerState: new RasterizerState(Cull: CullType.None),
            Name: "Cluster tone map and present");
    }

    internal ClusterComputeShader Traversal { get; }
    internal ClusterComputeShader CullClearPhase1 { get; }
    internal ClusterComputeShader CullPhase1 { get; }
    internal ClusterComputeShader CullClearPhase2 { get; }
    internal ClusterComputeShader CullPhase2 { get; }
    internal ClusterComputeShader RasterDeformBinReset { get; }
    internal ClusterComputeShader RasterDeformBinCount { get; }
    internal ClusterComputeShader RasterDeformBinReserve { get; }
    internal ClusterComputeShader RasterDeformBinScatter { get; }
    internal ClusterComputeShader DeformCachePopulate { get; }
    internal ClusterComputeShader SoftwareRaster { get; }
    internal ClusterRasterShader HardwareRaster { get; }
    internal ClusterRasterShader DepthMerge { get; }
    internal ClusterComputeShader HiZFirst { get; }
    internal ClusterComputeShader HiZDownsample { get; }
    internal ClusterComputeShader HiZDownsampleTwo { get; }
    internal ClusterComputeShader ShadeBinClearPrepare { get; }
    internal ClusterComputeShader ShadeBinCount { get; }
    internal ClusterComputeShader ShadeBinReserve { get; }
    internal ClusterComputeShader ShadeBinScatter { get; }
    internal ClusterComputeShader MotionVectors { get; }
    internal ClusterComputeShader Resolve { get; }
    internal ClusterRasterShader TemporalResolve { get; }
    internal ClusterRasterShader Tonemap { get; }

    internal AssetLoader Assets => _assets;

    internal Shader ReadMaterialShader(AssetGuid guid)
        => ReadShader(guid, "material pass");

    private ClusterComputeShader Compute(ClusterShaderOperationRole role, string name)
    {
        ClusterShaderOperation operation = Operation(role);
        if (string.IsNullOrWhiteSpace(operation.ComputeEntryPoint)
            || !string.IsNullOrWhiteSpace(operation.VertexEntryPoint)
            || !string.IsNullOrWhiteSpace(operation.PixelEntryPoint))
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' is not a compute operation.");
        }
        Shader shader = ReadShader(operation.Shader, role.ToString());
        ClusterComputeShader result = ClusterComputeShader.Create(
            _backend,
            _device,
            shader,
            operation.ComputeEntryPoint,
            name);
        _pipelines.Add(result);
        return result;
    }

    private ClusterRasterShader Raster(
        ClusterShaderOperationRole role,
        ReadOnlySpan<Format> ColorFormats,
        Format? DepthStencilFormat = null,
        RasterizerState RasterizerState = default,
        DepthStencilState DepthStencilState = default,
        ReadOnlySpan<BlendAttachmentState> BlendAttachments = default,
        uint SampleCount = 1,
        string? Name = null,
        uint SampleMask = uint.MaxValue,
        bool AlphaToCoverage = false)
    {
        ClusterShaderOperation operation = Operation(role);
        if (!string.IsNullOrWhiteSpace(operation.ComputeEntryPoint)
            || string.IsNullOrWhiteSpace(operation.VertexEntryPoint)
            || string.IsNullOrWhiteSpace(operation.PixelEntryPoint))
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' is not a raster operation.");
        }
        Shader shader = ReadShader(operation.Shader, role.ToString());
        ClusterRasterShader result = ClusterRasterShader.Create(
            _backend,
            _device,
            shader,
            operation.VertexEntryPoint,
            operation.PixelEntryPoint,
            ColorFormats,
            DepthStencilFormat,
            RasterizerState,
            DepthStencilState,
            BlendAttachments,
            SampleCount,
            Name,
            SampleMask,
            AlphaToCoverage);
        _pipelines.Add(result);
        return result;
    }

    private ClusterShaderOperation Operation(ClusterShaderOperationRole role)
        => _operations.TryGetValue(role, out ClusterShaderOperation? operation)
            ? operation
            : throw new InvalidDataException(
                $"Cluster render asset has no '{role}' shader operation.");

    private Shader ReadShader(ShaderAssetRef? reference, string field)
    {
        if (!AssetGuid.TryParse(reference?.ShaderGuid, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Cluster shader operation '{field}' has no shader.");
        return ReadShader(guid, field);
    }

    private Shader ReadShader(AssetGuid guid, string field)
    {
        if (_shaderCache.TryGetValue(guid, out Shader? existing))
            return existing;
        AssetHandle<Shader> handle = _assets.Load(new AssetId<Shader>(guid));
        if (handle.LoadState != AssetLoadState.Ready)
            _assets.WaitAsync(handle).AsTask().GetAwaiter().GetResult();
        AssetRead<Shader> read = _assets.Read(handle);
        _reads.Add(read);
        Shader shader = read.Value;
        if (!_shaderCache.TryAdd(guid, shader))
            throw new InvalidOperationException($"Shader cache publication failed for '{field}'.");
        return shader;
    }

    private static Dictionary<ClusterShaderOperationRole, ClusterShaderOperation> IndexOperations(
        ClusterShaders configuration)
    {
        IList<ClusterShaderOperation> operations = configuration.Operations
            ?? throw new InvalidDataException("Cluster render asset has no shader operations.");
        var result = new Dictionary<ClusterShaderOperationRole, ClusterShaderOperation>(
            operations.Count);
        foreach (ClusterShaderOperation operation in operations)
        {
            if (!result.TryAdd(operation.Role, operation))
            {
                throw new InvalidDataException(
                    $"Cluster render asset repeats shader operation '{operation.Role}'.");
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (int index = _pipelines.Count - 1; index >= 0; index--)
            TryDispose(_pipelines[index], ref failures);
        for (int index = _reads.Count - 1; index >= 0; index--)
            TryDispose(_reads[index], ref failures);
        _pipelines.Clear();
        _reads.Clear();
        _shaderCache.Clear();
        _operations.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

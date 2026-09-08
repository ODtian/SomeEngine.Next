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
    private static readonly int RequiredPipelineCount = Enum
        .GetValues<ClusterShaderOperationRole>()
        .Count(static role => role != ClusterShaderOperationRole.None);

    private readonly List<ClusterPipeline> _pipelines = [];
    private bool _disposed;

    internal ClusterPipelineSet(
        IGraphicsBackend backend,
        Device device,
        ClusterShaders configuration,
        ClusterVisibilityMode visibilityMode)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);
        _pipelines.EnsureCapacity(RequiredPipelineCount);
        try
        {
            Dictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations =
                IndexOperations(configuration);
            ClusterDefaultGeometryContract geometry = ResolveDefaultGeometryContract(operations);
            DefaultRasterVertexEncoding = geometry.VertexEncoding;
            DefaultRasterCullingEnabled = geometry.Bounds == ClusterConservativeBounds.Finite;

            ClusterComputePipeline Compute(
                ClusterShaderOperationRole role,
                string name,
                string? entryPoint = null) =>
                CreateCompute(backend, device, operations, _pipelines, role, name, entryPoint);

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
                    bool alphaToCoverage = false,
                    string? vertexEntry = null,
                    string? pixelEntry = null) =>
                    CreateRaster(
                        backend,
                        device,
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
                        alphaToCoverage,
                        vertexEntry,
                        pixelEntry);

            Traversal = Compute(ClusterShaderOperationRole.BvhTraversal, "Cluster BVH traversal");
            SceneBounds = Compute(ClusterShaderOperationRole.SceneBounds, "Cluster spatial scene bounds");

                TraversalInitialize = Compute(
                    ClusterShaderOperationRole.BvhTraversalInitialize,
                    "Cluster BVH traversal initialization");
                DeferredResume = Compute(
                    ClusterShaderOperationRole.BvhDeferredResume,
                    "Cluster deferred hierarchy resume");
                CullReset = Compute(ClusterShaderOperationRole.CullReset, "Cluster cull reset");
                CullPhase1 = Compute(ClusterShaderOperationRole.CullPhaseOne, "Cluster cull phase one");
                CullPhase2 = Compute(ClusterShaderOperationRole.CullPhaseTwo, "Cluster cull phase two");

                RasterBinReset = Compute(
                    ClusterShaderOperationRole.RasterBinningReset,
                    "Cluster raster bins reset");
                RasterBinCount = Compute(
                    ClusterShaderOperationRole.RasterBinningCount,
                    "Cluster raster bins count");
                RasterBinReserve = Compute(
                    ClusterShaderOperationRole.RasterBinningReserve,
                    "Cluster raster bins reserve");
                RasterBinScatter = Compute(
                    ClusterShaderOperationRole.RasterBinningScatter,
                    "Cluster raster bins scatter");
                TessellationBinReset = Compute(
                    ClusterShaderOperationRole.TessellationBinningReset,
                    "Cluster tessellation bins reset");
                TessellationBinCount = Compute(
                    ClusterShaderOperationRole.TessellationBinningCount,
                    "Cluster tessellation bins count");
                TessellationBinReserve = Compute(
                    ClusterShaderOperationRole.TessellationBinningReserve,
                    "Cluster tessellation bins reserve");
                TessellationBinScatter = Compute(
                    ClusterShaderOperationRole.TessellationBinningScatter,
                    "Cluster tessellation bins scatter");
                SoftwareRaster = visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? null
                    : Compute(
                        ClusterShaderOperationRole.SoftwareVisibilityRaster,
                        "Cluster software visibility raster",
                        DefaultSoftwareRasterEntry(visibilityMode));
                WinnerClear = visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? null
                    : Compute(
                        visibilityMode == ClusterVisibilityMode.ImageAtomic64
                            ? ClusterShaderOperationRole.VisibilityClearImage
                            : ClusterShaderOperationRole.VisibilityClearBuffer,
                        "Cluster visibility winner clear");

                HardwareRaster = Raster(
                    ClusterShaderOperationRole.HardwareVisibilityRaster,
                    visibilityMode == ClusterVisibilityMode.HardwareOnly
                        ? [Format.R32UInt]
                        : [],
                    depthStencilFormat: visibilityMode == ClusterVisibilityMode.HardwareOnly
                        ? Format.D32Float
                        : null,
                    rasterizerState: new RasterizerState(Cull: CullType.Back),
                    depthStencilState: visibilityMode == ClusterVisibilityMode.HardwareOnly
                        ? new DepthStencilState(
                            DepthTest: true,
                            DepthWrite: true,
                            DepthComparison: CompareOperation.LessOrEqual)
                        : default,
                    name: "Cluster hardware visibility raster",
                    vertexEntry: "VSVisBuffer",
                    pixelEntry: DefaultHardwarePixelEntry(visibilityMode, ClusterRasterPipeline.SupportsMesh(backend, device)));
                WinnerResolve = visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? null
                    : Raster(
                        visibilityMode == ClusterVisibilityMode.ImageAtomic64
                            ? ClusterShaderOperationRole.VisibilityResolveImage
                            : ClusterShaderOperationRole.VisibilityResolveBuffer,
                        [Format.R32UInt],
                        depthStencilFormat: Format.D32Float,
                        rasterizerState: new RasterizerState(Cull: CullType.None),
                        depthStencilState: new DepthStencilState(
                            DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.Always),
                        name: "Cluster visibility winner resolve");

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
                Resolve = Compute(ClusterShaderOperationRole.VisibilityResolve, "Cluster visibility resolve");
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
    internal ClusterComputePipeline SceneBounds { get; }
    internal ClusterComputePipeline TraversalInitialize { get; }
    internal ClusterComputePipeline DeferredResume { get; }
    internal string DefaultRasterVertexEncoding { get; }
    internal bool DefaultRasterCullingEnabled { get; }
    internal ClusterComputePipeline CullReset { get; }
    internal ClusterComputePipeline CullPhase1 { get; }
    internal ClusterComputePipeline CullPhase2 { get; }
    internal ClusterComputePipeline RasterBinReset { get; }
    internal ClusterComputePipeline RasterBinCount { get; }
    internal ClusterComputePipeline RasterBinReserve { get; }
    internal ClusterComputePipeline RasterBinScatter { get; }
    internal ClusterComputePipeline TessellationBinReset { get; }
    internal ClusterComputePipeline TessellationBinCount { get; }
    internal ClusterComputePipeline TessellationBinReserve { get; }
    internal ClusterComputePipeline TessellationBinScatter { get; }
    internal ClusterComputePipeline? SoftwareRaster { get; }
    internal ClusterComputePipeline? WinnerClear { get; }
    internal ClusterRasterPipeline HardwareRaster { get; }
    internal ClusterRasterPipeline? WinnerResolve { get; }
    internal ClusterComputePipeline HiZFirst { get; }
    internal ClusterComputePipeline HiZDownsample { get; }
    internal ClusterComputePipeline HiZDownsampleTwo { get; }
    internal ClusterComputePipeline ShadeBinClearPrepare { get; }
    internal ClusterComputePipeline ShadeBinCount { get; }
    internal ClusterComputePipeline ShadeBinReserve { get; }
    internal ClusterComputePipeline ShadeBinScatter { get; }
    internal ClusterComputePipeline Resolve { get; }

    private static ClusterComputePipeline CreateCompute(
        IGraphicsBackend backend,
        Device device,
        IReadOnlyDictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations,
        List<ClusterPipeline> ownedPipelines,
        ClusterShaderOperationRole role,
        string name,
        string? entryPoint = null)
    {
        ClusterShaderOperation operation = GetOperation(operations, role);
        ShaderRef compute = GetShader(operation, role, ShaderStage.Compute);

        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterComputePipeline? created = null;
        try
        {
            Shader shader = compute.Asset ?? throw new InvalidDataException(
                $"Cluster shader operation '{role}' has no resolved shader asset.");
            created = ClusterComputePipeline.Create(
                backend,
                device,
                shader,
                entryPoint ?? compute.EntryPoint!,
                name);
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
        bool alphaToCoverage,
        string? vertexEntry = null,
        string? pixelEntry = null)
    {
        ClusterShaderOperation operation = GetOperation(operations, role);
        ShaderRef vertex = GetShader(operation, role, ShaderStage.Vertex);
        ShaderRef pixel = GetShader(operation, role, ShaderStage.Pixel);
        AssetGuid vertexGuid = GetShaderGuid(vertex, role);
        AssetGuid pixelGuid = GetShaderGuid(pixel, role);
        if (vertexGuid != pixelGuid)
            throw new InvalidDataException($"Cluster shader operation '{role}' spans shader assets.");

        ownedPipelines.EnsureCapacity(checked(ownedPipelines.Count + 1));
        ClusterRasterPipeline? created = null;
        try
        {
            Shader shader = vertex.Asset ?? throw new InvalidDataException(
                $"Cluster shader operation '{role}' has no resolved shader asset.");
            if (!ReferenceEquals(shader, pixel.Asset))
                throw new InvalidDataException($"Cluster shader operation '{role}' has inconsistent resolved shader assets.");
            bool usesMesh = role == ClusterShaderOperationRole.HardwareVisibilityRaster &&
                ClusterRasterPipeline.SupportsMesh(backend, device);
            created = ClusterRasterPipeline.Create(
                backend,
                device,
                shader,
                usesMesh ? "MSVisBuffer" : vertexEntry ?? vertex.EntryPoint!,
                pixelEntry ?? pixel.EntryPoint!,
                colorFormats,
                depthStencilFormat,
                rasterizerState,
                depthStencilState,
                blendAttachments,
                sampleCount,
                name,
                sampleMask,
                alphaToCoverage,
                geometryStage: usesMesh ? ShaderStage.Mesh : ShaderStage.Vertex);
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

    private static ClusterDefaultGeometryContract ResolveDefaultGeometryContract(
        IReadOnlyDictionary<ClusterShaderOperationRole, ClusterShaderOperation> operations)
    {
        string? vertexEncoding = null;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;
        foreach (ClusterShaderOperation operation in operations.Values)
        {
            if (!ClusterShaders.IsPositionOperation(operation.Role))
                continue;
            ShaderStage stage = IsRasterOperation(operation.Role)
                ? ShaderStage.Vertex
                : ShaderStage.Compute;
            ShaderRef shaderReference = GetShader(operation, operation.Role, stage);
            Shader shader = shaderReference.Asset ?? throw new InvalidDataException(
                $"Cluster shader operation '{operation.Role}' has no resolved shader asset.");
            string entryPoint = shaderReference.EntryPoint!;
            string subject = $"Cluster shader operation '{operation.Role}'";
            string currentEncoding = ClusterGeometryContracts.RequireVertexEncoding(
                shader,
                entryPoint,
                subject);
            if (vertexEncoding is not null &&
                !string.Equals(vertexEncoding, currentEncoding, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{subject} accepts vertex encoding '{currentEncoding}', but the Cluster pipeline " +
                    $"already requires '{vertexEncoding}'.");
            }
            vertexEncoding = currentEncoding;
            if (ClusterGeometryContracts.RequireConservativeBounds(shader, entryPoint, subject) ==
                ClusterConservativeBounds.Unbounded)
            {
                bounds = ClusterConservativeBounds.Unbounded;
            }
        }
        return new ClusterDefaultGeometryContract(
            vertexEncoding ?? throw new InvalidDataException(
                "Cluster render asset has no vertex-evaluating shader operation."),
            bounds);
    }

    private static ShaderRef GetShader(
        ClusterShaderOperation operation,
        ClusterShaderOperationRole role,
        ShaderStage stage)
    {
        IList<ShaderRef> shaders = operation.Shaders
            ?? throw new InvalidDataException(
                $"Cluster shader operation '{role}' has no shader entries.");
        ShaderRef? result = null;
        foreach (ShaderRef shader in shaders)
        {
            if (shader is null || shader.Stage != stage)
                continue;
            if (result is not null)
            {
                throw new InvalidDataException(
                    $"Cluster shader operation '{role}' repeats the {stage} stage.");
            }
            result = shader;
        }
        if (result is null || string.IsNullOrWhiteSpace(result.EntryPoint))
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' has no {stage} shader entry.");
        }
        return result;
    }

    private static AssetGuid GetShaderGuid(
        ShaderRef shader,
        ClusterShaderOperationRole role)
    {
        if (!AssetGuid.TryParse(shader.AssetGuid, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Cluster shader operation '{role}' has no shader asset.");
        return guid;
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
            bool isRaster = IsRasterOperation(operation.Role);
            IList<ShaderRef>? shaders = operation.Shaders;
            bool validShape = shaders is not null && (isRaster
                ? shaders.Count == 2 &&
                    shaders.Count(shader => shader is not null && shader.Stage == ShaderStage.Vertex) == 1 &&
                    shaders.Count(shader => shader is not null && shader.Stage == ShaderStage.Pixel) == 1
                : shaders.Count == 1 &&
                    shaders[0] is not null &&
                    shaders[0].Stage == ShaderStage.Compute);
            if (!validShape)
            {
                throw new InvalidDataException(
                    $"Cluster shader operation '{operation.Role}' has an invalid shader-stage set.");
            }
            AssetGuid? shaderGuid = null;
            foreach (ShaderRef shader in shaders!)
            {
                AssetGuid current = GetShaderGuid(shader, operation.Role);
                if (string.IsNullOrWhiteSpace(shader.EntryPoint))
                {
                    throw new InvalidDataException(
                        $"Cluster shader operation '{operation.Role}' has an empty entry point.");
                }
                if (shaderGuid.HasValue && shaderGuid.Value != current)
                {
                    throw new InvalidDataException(
                        $"Cluster shader operation '{operation.Role}' spans shader assets.");
                }
                shaderGuid = current;
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
        ClusterShaderOperationRole.VisibilityResolveImage or
        ClusterShaderOperationRole.VisibilityResolveBuffer;

    private static string DefaultSoftwareRasterEntry(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => "CSSWRasterImageAtomic64",
        ClusterVisibilityMode.BufferAtomic64 => "CSSWRasterBufferAtomic64",
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException(
            "Hardware-only visibility has no software raster entry."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string DefaultHardwarePixelEntry(ClusterVisibilityMode mode, bool mesh) => (mode, mesh) switch
    {
        (ClusterVisibilityMode.ImageAtomic64, false) => "PSVisBufferImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, false) => "PSVisBufferBufferAtomic64",
        (ClusterVisibilityMode.HardwareOnly, false) => "PSVisBufferHardwareOnly",
        (ClusterVisibilityMode.ImageAtomic64, true) => "PSMeshVisBufferImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, true) => "PSMeshVisBufferBufferAtomic64",
        (ClusterVisibilityMode.HardwareOnly, true) => "PSMeshVisBufferHardwareOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };


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

internal readonly record struct ClusterDefaultGeometryContract(
    string VertexEncoding,
    ClusterConservativeBounds Bounds);

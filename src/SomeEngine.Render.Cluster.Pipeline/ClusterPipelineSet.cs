using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the complete executable pipeline set selected by one Cluster render asset. Source paths
/// and register numbers never enter frame execution; all descriptors are linked from cooked
/// entry-point reflection.
/// </summary>
internal sealed partial class ClusterPipelineSet : IDisposable
{
    private static readonly int RequiredPipelineCount = Enum
        .GetValues<ClusterShaderOperationRole>()
        .Count(static role => role != ClusterShaderOperationRole.None);

    private readonly List<IDisposable> _pipelines = [];
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<(bool TargetFilters, bool MultipleViews), LinkedComputePipeline> _traversals = [];
    private readonly Func<bool, bool, LinkedComputePipeline> _createTraversal;
    private bool _disposed;

    internal ClusterShaders Shaders { get; }
    internal ClusterVisibilityMode VisibilityMode { get; }

    internal ClusterPipelineSet(
        IGraphicsBackend backend,
        Device device,
        ClusterShaders configuration,
        ClusterVisibilityMode visibilityMode)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);
        _backend = backend;
        _device = device;
        Shaders = configuration;
        VisibilityMode = visibilityMode;
        _pipelines.EnsureCapacity(RequiredPipelineCount);
        try
        {
            Dictionary<ClusterShaderOperationRole, ShaderOperation> operations =
                IndexOperations(configuration);
            ClusterDefaultGeometry geometry = ResolveDefaultGeometry(operations);
            DefaultRasterVertexEncoding = geometry.VertexEncoding;
            DefaultRasterCullingEnabled = geometry.Bounds == ClusterConservativeBounds.Finite;

            LinkedComputePipeline Compute(
                ClusterShaderOperationRole role,
                string name,
                string? entryPoint = null,
                string[]? specializations = null) =>
                CreateCompute(backend, device, operations, role, name, entryPoint, specializations);

                LinkedRasterPipeline Raster(
                    ClusterShaderOperationRole role,
                    ReadOnlySpan<Format> colorFormats,
                    Format? depthStencilFormat = null,
                    RasterizerState rasterizerState = default,
                    DepthStencilState depthStencilState = default,
                    string? name = null,
                    string? vertexEntry = null,
                    string? pixelEntry = null) =>
                    CreateRaster(
                        backend,
                        device,
                        operations,
                        role,
                        colorFormats,
                        depthStencilFormat,
                        rasterizerState,
                        depthStencilState,
                        name,
                        vertexEntry,
                        pixelEntry);

            _createTraversal = (targetFilters, multipleViews) => Compute(
                ClusterShaderOperationRole.BvhTraversal,
                $"Cluster BVH traversal (target filters: {targetFilters}, multiple views: {multipleViews})",
                specializations: [targetFilters ? "true" : "false", multipleViews ? "true" : "false"]);
            DecalClear = Compute(ClusterShaderOperationRole.MaterialDecalCells, "Clear material decal cells", "ClearMaterialDecalCells");
            DecalCount = Compute(ClusterShaderOperationRole.MaterialDecalCells, "Count material decal cells", "CountMaterialDecalCells");
            DecalReserve = Compute(ClusterShaderOperationRole.MaterialDecalCells, "Reserve material decal cells", "ReserveMaterialDecalCells");
            DecalScatter = Compute(ClusterShaderOperationRole.MaterialDecalCells, "Scatter material decal cells", "ScatterMaterialDecalCells");
            DecalSort = Compute(ClusterShaderOperationRole.MaterialDecalCells, "Sort material decal cells", "SortMaterialDecalCells");
            LeafFlagFixups = Compute(ClusterShaderOperationRole.LeafFlagFixups, "Cluster streaming leaf flags");
            PageRequests = Compute(ClusterShaderOperationRole.PageRequests, "Cluster page request priorities");
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
                    pixelEntry: DefaultHardwarePixelEntry(visibilityMode, ClusterRasterPolicy.SupportsMesh(backend, device)));
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
                ShadeBinCount = Compute(ClusterShaderOperationRole.MaterialBinningCount, "Cluster material bin count");
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

    internal LinkedComputePipeline GetTraversal(bool targetFilters, bool multipleViews)
    {
        var capabilities = (targetFilters, multipleViews);
        if (!_traversals.TryGetValue(capabilities, out LinkedComputePipeline? pipeline))
        {
            pipeline = _createTraversal(targetFilters, multipleViews);
            _traversals.Add(capabilities, pipeline);
        }
        return pipeline;
    }
    internal LinkedComputePipeline PageRequests { get; }
    internal LinkedComputePipeline LeafFlagFixups { get; }
    internal LinkedComputePipeline SceneBounds { get; }
    internal LinkedComputePipeline TraversalInitialize { get; }
    internal LinkedComputePipeline DeferredResume { get; }
    internal string DefaultRasterVertexEncoding { get; }
    internal bool DefaultRasterCullingEnabled { get; }
    internal LinkedComputePipeline DecalClear { get; }
    internal LinkedComputePipeline DecalCount { get; }
    internal LinkedComputePipeline DecalReserve { get; }
    internal LinkedComputePipeline DecalScatter { get; }
    internal LinkedComputePipeline DecalSort { get; }
    internal LinkedComputePipeline CullReset { get; }
    internal LinkedComputePipeline CullPhase1 { get; }
    internal LinkedComputePipeline CullPhase2 { get; }
    internal LinkedComputePipeline RasterBinReset { get; }
    internal LinkedComputePipeline RasterBinCount { get; }
    internal LinkedComputePipeline RasterBinReserve { get; }
    internal LinkedComputePipeline RasterBinScatter { get; }
    internal LinkedComputePipeline TessellationBinReset { get; }
    internal LinkedComputePipeline TessellationBinCount { get; }
    internal LinkedComputePipeline TessellationBinReserve { get; }
    internal LinkedComputePipeline TessellationBinScatter { get; }
    internal LinkedComputePipeline? SoftwareRaster { get; }
    internal LinkedComputePipeline? WinnerClear { get; }
    internal LinkedRasterPipeline HardwareRaster { get; }
    internal LinkedRasterPipeline? WinnerResolve { get; }
    internal LinkedComputePipeline HiZFirst { get; }
    internal LinkedComputePipeline HiZDownsample { get; }
    internal LinkedComputePipeline HiZDownsampleTwo { get; }
    internal LinkedComputePipeline ShadeBinClearPrepare { get; }
    internal LinkedComputePipeline ShadeBinCount { get; }
    internal LinkedComputePipeline ShadeBinReserve { get; }
    internal LinkedComputePipeline ShadeBinScatter { get; }
    internal LinkedComputePipeline Resolve { get; }

    private LinkedComputePipeline CreateCompute(
        IGraphicsBackend backend,
        Device device,
        IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations,
        ClusterShaderOperationRole role,
        string name,
        string? entryPoint = null,
        string[]? specializations = null)
    {
        ShaderOperation operation = GetOperation(operations, role);
        (Shader Shader, EntryPointReflection Entry) compute =
            GetEntry(operation, role, SlangStage.Compute);
        string selectedEntry = entryPoint ?? compute.Entry.Name;
        if (specializations is not null && compute.Shader.HasSpecializations(selectedEntry))
        {
            Shader shader = compute.Shader.RequireSpecialization(selectedEntry, specializations);
            compute = (shader, shader.RequireSlangEntry(shader.RequireSlangTarget(device.Capabilities.CompileTarget),
                selectedEntry, SlangStage.Compute));
        }

        _pipelines.EnsureCapacity(checked(_pipelines.Count + 1));
        LinkedComputePipeline created = entryPoint is null
            ? LinkedComputePipeline.Create(
                backend,
                device,
                compute.Shader,
                compute.Entry,
                name)
            : LinkedComputePipeline.Create(
                backend,
                device,
                compute.Shader,
                entryPoint,
                name);
        _pipelines.Add(created);
        return created;
    }

    private LinkedRasterPipeline CreateRaster(
        IGraphicsBackend backend,
        Device device,
        IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations,
        ClusterShaderOperationRole role,
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat,
        RasterizerState rasterizerState,
        DepthStencilState depthStencilState,
        string? name,
        string? vertexEntry = null,
        string? pixelEntry = null)
    {
        ShaderOperation operation = GetOperation(operations, role);
        (Shader Shader, EntryPointReflection Entry) vertex =
            GetEntry(operation, role, SlangStage.Vertex);
        (Shader Shader, EntryPointReflection Entry) pixel =
            GetEntry(operation, role, SlangStage.Fragment);
        _pipelines.EnsureCapacity(checked(_pipelines.Count + 1));
        Shader shader = vertex.Shader;
        bool usesMesh = role == ClusterShaderOperationRole.HardwareVisibilityRaster &&
            ClusterRasterPolicy.SupportsMesh(backend, device);
        string geometryEntry = vertexEntry ?? vertex.Entry.Name;
        if (usesMesh)
            geometryEntry = ClusterRasterPolicy.SupportsMeshlets(backend, device) &&
                ClusterMaterialEntries.HasEntry(
                    shader,
                    device.Capabilities.CompileTarget,
                    "MSVisBufferMeshlet",
                    SlangStage.Mesh)
                    ? "MSVisBufferMeshlet" : "MSVisBuffer";
        if (role == ClusterShaderOperationRole.HardwareVisibilityRaster)
        {
            EntryPointReflection rasterPixel = shader.RequireSlangEntry(
                shader.RequireSlangTarget(device.Capabilities.CompileTarget),
                pixelEntry ?? pixel.Entry.Name, SlangStage.Fragment);
            rasterizerState = rasterizerState with
            {
                Cull = ClusterGeometryRequirements.UsesBothRasterFaces(rasterPixel) ? CullType.None : CullType.Back,
            };
        }
        LinkedRasterPipeline created = LinkedRasterPipeline.Create(
            backend,
            device,
            shader,
            usesMesh ? SlangStage.Mesh : SlangStage.Vertex,
            geometryEntry,
            pixelEntry ?? pixel.Entry.Name,
            colorFormats,
            depthStencilFormat,
            rasterizerState,
            depthStencilState,
            label: name);
        _pipelines.Add(created);
        return created;
    }

    private static ShaderOperation GetOperation(
        IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations,
        ClusterShaderOperationRole role)
        => operations.TryGetValue(role, out ShaderOperation? operation)
            ? operation
            : throw new InvalidDataException(
                $"Cluster render asset has no '{role}' shader operation.");

    private ClusterDefaultGeometry ResolveDefaultGeometry(
        IReadOnlyDictionary<ClusterShaderOperationRole, ShaderOperation> operations)
    {
        string? vertexEncoding = null;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;
        foreach (ShaderOperation operation in operations.Values)
        {
            if (!ClusterShaders.IsPositionOperation(operation.Role))
                continue;
            SlangStage stage = IsRasterOperation(operation.Role)
                ? SlangStage.Vertex
                : SlangStage.Compute;
            (Shader Shader, EntryPointReflection Entry) shaderReference =
                GetEntry(operation, operation.Role, stage);
            Shader shader = shaderReference.Shader;
            string entryPoint = shaderReference.Entry.Name;
            string subject = $"Cluster shader operation '{operation.Role}'";
            string currentEncoding = shader.RequireSlangEntryStringAttribute(
                0, entryPoint, "VertexEncoding", subject);
            if (vertexEncoding is not null &&
                !string.Equals(vertexEncoding, currentEncoding, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{subject} accepts vertex encoding '{currentEncoding}', but the Cluster pipeline " +
                    $"already requires '{vertexEncoding}'.");
            }
            vertexEncoding = currentEncoding;
            if (ClusterGeometryRequirements.RequireConservativeBounds(shader, entryPoint, subject) ==
                ClusterConservativeBounds.Unbounded)
            {
                bounds = ClusterConservativeBounds.Unbounded;
            }
        }
        return new ClusterDefaultGeometry(
            vertexEncoding ?? throw new InvalidDataException(
                "Cluster render asset has no vertex-evaluating shader operation."),
            bounds);
    }

    private static (Shader Shader, EntryPointReflection Entry) GetEntry(
        ShaderOperation operation,
        ClusterShaderOperationRole role,
        SlangStage stage)
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
                throw new InvalidDataException(
                    $"Cluster shader operation '{role}' repeats the {stage} stage.");
            }
            resultIndex = index;
        }
        if (resultIndex < 0)
        {
            throw new InvalidDataException(
                $"Cluster shader operation '{role}' has no {stage} shader entry.");
        }
        return (operation.Shaders[resultIndex], operation.Entries[resultIndex]);
    }

    private static Dictionary<ClusterShaderOperationRole, ShaderOperation> IndexOperations(
        ClusterShaders configuration)
    {
        IList<ShaderOperation> operations = configuration.Operations
            ?? throw new InvalidDataException("Cluster render asset has no shader operations.");
        if (operations.Count != RequiredPipelineCount)
        {
            throw new InvalidDataException(
                $"Cluster render asset must define exactly {RequiredPipelineCount} shader operations; " +
                $"it defines {operations.Count}.");
        }
        var result = new Dictionary<ClusterShaderOperationRole, ShaderOperation>(
            operations.Count);
        foreach (ShaderOperation operation in operations)
        {
            if (operation is null ||
                operation.Role == ClusterShaderOperationRole.None ||
                !Enum.IsDefined(operation.Role))
            {
                throw new InvalidDataException(
                    "Cluster render asset contains an invalid shader-operation role.");
            }
            bool isRaster = IsRasterOperation(operation.Role);
            IList<Shader> shaders = operation.Shaders;
            IList<EntryPointReflection> entries = operation.Entries;
            bool validShape = shaders.Count == entries.Count && (isRaster
                ? entries.Count == 2 &&
                    entries.Count(static entry => entry.Stage == SlangStage.Vertex) == 1 &&
                    entries.Count(static entry => entry.Stage == SlangStage.Fragment) == 1
                : entries.Count == 1 && entries[0].Stage == SlangStage.Compute);
            if (!validShape)
            {
                throw new InvalidDataException(
                    $"Cluster shader operation '{operation.Role}' has an invalid shader-stage set.");
            }
            Shader? selectedShader = null;
            for (int index = 0; index < shaders.Count; index++)
            {
                Shader current = shaders[index];
                EntryPointReflection entry = entries[index];
                if (entry == EntryPointReflection.Null || string.IsNullOrWhiteSpace(entry.Name))
                {
                    throw new InvalidDataException(
                        $"Cluster shader operation '{operation.Role}' has an empty entry point.");
                }
                if (selectedShader is not null && !ReferenceEquals(selectedShader, current))
                {
                    throw new InvalidDataException(
                        $"Cluster shader operation '{operation.Role}' spans shader assets.");
                }
                selectedShader = current;
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
        DisposeMaterialPipelines(ref failures);
        for (int index = _pipelines.Count - 1; index >= 0; index--)
            TryDispose(_pipelines[index], ref failures);
        _pipelines.Clear();
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

internal readonly record struct ClusterDefaultGeometry(
    string VertexEncoding,
    ClusterConservativeBounds Bounds);

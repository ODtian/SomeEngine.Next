using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterRendererSystem
{
    private readonly FrameResourceScratch _frameResourceScratch = new();

    internal const int CandidateCountReadbackOffset = 0;
    internal const int CandidateArgsReadbackOffset = 4;
    internal const int DrawArgsReadbackOffset = 16;
    internal const int Phase2CandidateCountReadbackOffset = 32;
    internal const int Phase2CandidateArgsReadbackOffset = 36;
    internal const int Phase2DrawArgsReadbackOffset = 48;
    internal const int RasterReserveReadbackOffset = 64;
    internal const int ShadeReserveReadbackOffset = 72;
    internal const int DeformReserveReadbackOffset = 76;
    internal const int CacheAllocationReadbackOffset = 80;
    internal const int CachedDeformClustersReadbackOffset = 84;
    internal const int SoftwareDebugReadbackOffset = 88;
    internal const int ShadeBinCountReadbackOffset = 92;
    internal const int VisibilityProbeReadbackOffset = 512;
    internal const int VisibilityProbePixelCount = 64;
    internal const int VisibilityProbeRowPitch = VisibilityProbePixelCount * sizeof(uint);
    internal const int FrameMetricsReadbackByteSize =
        VisibilityProbeReadbackOffset + VisibilityProbeRowPitch;
    private const uint MaxBinnedEntriesPerCluster = 9;
    private const uint ClusterVertexCapacity = 64;
    private const int RasterBinStride = 32;
    private const int DeformBinStride = 16;
    private const int VisibleClusterStride = 16;
    private const int CandidateStride = 12;
    private const int BinnedClusterStride = 8;
    private const int LightStride = 144;
    private const int LightTileSize = 16;

    private FrameResources CreateFrameResources(
        ref RenderGraphFrame graph,
        in ClusterRenderTarget target,
        in ClusterRenderBinding binding,
        ClusterMaterialSnapshot snapshot,
        LightCollector lights)
    {
        uint materialCount = snapshot.MaterialCount;
        ulong candidates = _options.MaxCandidates;
        ulong binnedCapacity = checked(candidates * MaxBinnedEntriesPerCluster);
        int width = target.Width;
        int height = target.Height;

        var frame = new FrameResources(_frameResourceScratch)
        {
            Width = width,
            Height = height,
            MaterialCount = materialCount,
            SlotCapacity = snapshot.SlotCapacity,
            PageFaultCapacity = binding.PageFaultCapacity,
            PageHeap = ImportBuffer(
                ref graph,
                binding.PageHeap,
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                ResourceContentState.Defined),
            Bvh = ImportBuffer(
                ref graph,
                binding.Bvh,
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                ResourceContentState.Defined),
            InstanceData = ImportBuffer(
                ref graph,
                binding.PropertyData,
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                ResourceContentState.Defined),
            InstanceProperties = ImportBuffer(
                ref graph,
                binding.InstancePropertyMetadata,
                PipelineSync.AllShading,
                ResourceAccess.ConstantBuffer,
                ResourceContentState.Defined),
            InstancePropertiesRange = binding.InstancePropertyMetadataRange,
            PageFaultReadback = ImportBuffer(
                ref graph,
                _pageFaultReadbacks[RequireReadbackWriteGeneration()]
                    ?? throw new InvalidOperationException("Cluster page-fault readback was not created."),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceContentState.Undefined,
                _readbackFences[RequireReadbackWriteGeneration()]),
            Target = graph.GetImported(target.Texture),
            PreviousHiZ = graph.Import(
                _history!.PreviousHiZ,
                _history.PreviousHiZEndpoints),
            CurrentHiZ = graph.Import(
                _history.CurrentHiZ,
                _history.CurrentHiZEndpoints),
            PreviousScene = graph.Import(
                _history.PreviousScene,
                _history.PreviousSceneEndpoints),
            CurrentSceneHistory = graph.Import(
                _history.CurrentScene,
                _history.CurrentSceneEndpoints),
            PreviousMotion = graph.Import(
                _history.PreviousMotion,
                _history.PreviousMotionEndpoints),
            CurrentMotionHistory = graph.Import(
                _history.CurrentMotion,
                _history.CurrentMotionEndpoints),
            PreviousDepth = graph.Import(
                _history.PreviousDepth,
                _history.PreviousDepthEndpoints),
            CurrentDepthHistory = graph.Import(
                _history.CurrentDepth,
                _history.CurrentDepthEndpoints),
            SlotBuffer = UploadWords(
                ref graph,
                snapshot.SlotWords.Span,
                BufferUsages.ShaderRead,
                "Cluster material slot table"),
            ReadOffsetZero = UploadBytes(
                ref graph,
                stackalloc byte[16],
                BufferUsages.ShaderRead,
                "Cluster zero read offset"),
        };
        if (_options.EnableFrameMetricsReadback)
        {
            frame.FrameMetricsReadback = ImportBuffer(
                ref graph,
                _frameMetricReadbacks[RequireReadbackWriteGeneration()]
                    ?? throw new InvalidOperationException("Cluster frame-metrics readback was not created."),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceContentState.Undefined,
                _readbackFences[RequireReadbackWriteGeneration()]);
        }

        frame.CandidateArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect | BufferUsages.CopySource,
            "Cluster candidate dispatch");
        frame.CandidateArgsInitialization = UploadWords(
            ref graph,
            [0u, 1u, 1u, 0u],
            BufferUsages.CopySource,
            "Cluster candidate dispatch initialization");
        frame.CandidateClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * CandidateStride),
            BufferUsages.None,
            "Cluster candidates");
        frame.CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster candidate count");
        frame.DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-one counts");
        frame.PageFaults = frame.AddBuffer(
            ref graph,
            checked(sizeof(uint) + (ulong)binding.PageFaultCapacity * sizeof(uint)),
            BufferUsages.CopySource,
            "Cluster page faults");
        frame.Phase2CandidateArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect | BufferUsages.CopySource,
            "Cluster phase-two dispatch");
        frame.Phase2CandidateClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * CandidateStride),
            BufferUsages.None,
            "Cluster phase-two candidates");
        frame.Phase2CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster phase-two count");
        frame.Phase2DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-two counts");
        frame.VisibleClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * VisibleClusterStride),
            BufferUsages.None,
            "Cluster visible clusters");
        frame.RasterBinMeta = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * RasterBinStride),
            BufferUsages.None,
            "Cluster raster bins");
        frame.BinnedClusters = frame.AddBuffer(
            ref graph,
            checked(binnedCapacity * BinnedClusterStride),
            BufferUsages.None,
            "Cluster raster bin indices");
        frame.BinnedDrawArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.None,
            "Cluster raster draw metadata");
        frame.BinnedHardwareDrawArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.CopySource,
            "Cluster hardware draw metadata");
        frame.HardwareIndirectArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.Indirect | BufferUsages.CopyDestination,
            "Cluster hardware indirect arguments");
        frame.BinningDispatchArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect,
            "Cluster binning dispatch");
        frame.SoftwareDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * 2 * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster software raster dispatches");
        frame.RasterReserveCounters = frame.AddBuffer(ref graph, 4 * sizeof(uint), BufferUsages.CopySource, "Cluster raster reserve counters");
        frame.DeformBinMeta = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * DeformBinStride),
            BufferUsages.None,
            "Cluster deform bins");
        frame.DeformBinnedClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * BinnedClusterStride),
            BufferUsages.None,
            "Cluster deform bin indices");
        frame.DeformDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster deform dispatches");
        frame.DeformReserveCounters = frame.AddBuffer(ref graph, 2 * sizeof(uint), BufferUsages.CopySource, "Cluster deform reserve counters");
        frame.CacheOffsets = frame.AddBuffer(
            ref graph,
            checked(candidates * sizeof(uint)),
            BufferUsages.None,
            "Cluster deform cache offsets");
        frame.CacheAllocationCounter = frame.AddBuffer(ref graph, 2 * sizeof(uint), BufferUsages.CopySource, "Cluster deform cache statistics");
        frame.DeformCache = frame.AddBuffer(
            ref graph,
            _options.DeformCacheBytes,
            BufferUsages.None,
            "Cluster deform cache");
        frame.SoftwareDebug = frame.AddBuffer(
            ref graph,
            4 + 8192ul * 24,
            BufferUsages.CopySource,
            "Cluster software raster debug output");

        frame.ShadeBinCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade bin counts");
        frame.ShadeBinOffsets = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade bin offsets");
        frame.ShadeIndirectArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster shade dispatches");
        frame.ShadeScatterCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)materialCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade scatter counts");
        frame.ShadeReserveCounters = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster shade reserve counter");
        frame.PixelCoordinates = frame.AddBuffer(
            ref graph,
            checked((ulong)width * (ulong)height * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade pixel coordinates");

        frame.VisBuffer = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment |
            TextureUsages.CopySource,
            label: "Cluster visibility buffer"));
        frame.Depth = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.D32Float,
            TextureUsages.Sampled | TextureUsages.DepthStencilAttachment | TextureUsages.CopySource,
            label: "Cluster scene depth"));
        frame.SoftwareDepth = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
            label: "Cluster software depth"));
        frame.SceneColor = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R16G16B16A16Float,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Cluster scene color"));
        frame.MotionVectors = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R16G16Float,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Cluster motion vectors"));
        frame.TemporalColor = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R16G16B16A16Float,
            TextureUsages.Sampled | TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Cluster temporal scene color"));

        ImportMaterials(ref graph, ref frame);
        CreateLightResources(ref graph, ref frame, lights);
        RecordBufferInitialization(ref graph, in frame);
        RecordTargetInitialization(ref graph, in frame);
        return frame;
    }

    private void ImportMaterials(
        ref RenderGraphFrame graph,
        ref FrameResources frame)
    {
        ReadOnlySpan<ClusterMaterialGpuBinding> bindings = _materialBindings!.Bindings;
        if ((uint)bindings.Length != frame.MaterialCount)
        {
            throw new InvalidOperationException(
                "Cluster GPU bindings do not match the published material topology.");
        }
        foreach (ClusterMaterialGpuBinding material in bindings)
        {
            GraphTextureId albedo = ImportMaterialTexture(
                ref graph,
                in frame,
                material,
                "AlbedoMap");
            GraphTextureId normal = material.ParameterKind == ClusterMaterialParameterKind.StandardPbr
                ? ImportMaterialTexture(
                    ref graph,
                    in frame,
                    material,
                    "NormalMap")
                : default;
            GraphTextureId arm = material.ParameterKind == ClusterMaterialParameterKind.StandardPbr
                ? ImportMaterialTexture(
                    ref graph,
                    in frame,
                    material,
                    "ARMMap")
                : default;
            frame.AddMaterial(new MaterialResources(
                material,
                ImportBuffer(
                    ref graph,
                    material.ScalarBuffer,
                    PipelineSync.AllShading,
                    ResourceAccess.ShaderResource,
                    ResourceContentState.Defined),
                albedo,
                normal,
                arm));
        }
        frame.CookieAtlas = ImportTexture(
            ref graph,
            _materialBindings.CookieAtlas,
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            TextureLayout.ShaderResource,
            ResourceContentState.Defined);
    }

    private static GraphTextureId ImportMaterialTexture(
        ref RenderGraphFrame graph,
        in FrameResources frame,
        ClusterMaterialGpuBinding material,
        string name)
    {
        if (!material.Textures.TryGetValue(name, out Texture? handle) || handle is null)
            throw new InvalidOperationException($"Cluster material '{material.Name}' has no '{name}' texture.");
        if (frame.TryGetImportedTexture(handle, out GraphTextureId imported))
            return imported;

        GraphTextureId logical = ImportTexture(
            ref graph,
            handle,
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            TextureLayout.ShaderResource,
            ResourceContentState.Defined);
        frame.AddImportedTexture(handle, logical);
        return logical;
    }

    private void CreateLightResources(
        ref RenderGraphFrame graph,
        ref FrameResources frame,
        LightCollector source)
    {
        _gpuLights.Clear();
        _gpuLights.EnsureCapacity(
            source.Directional.Count + source.Points.Count + source.Spots.Count);
        foreach (RenderDirectionalLight light in source.Directional)
        {
            _gpuLights.Add(new ClusterGpuLight
            {
                Direction = NormalizeOrZero(light.Direction),
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            });
        }
        foreach (RenderPointLight light in source.Points)
        {
            _gpuLights.Add(new ClusterGpuLight
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            });
        }
        foreach (RenderSpotLight light in source.Spots)
        {
            _gpuLights.Add(new ClusterGpuLight
            {
                Position = light.Position,
                Range = light.Range,
                Direction = NormalizeOrZero(light.Direction),
                InnerConeCos = light.InnerConeCos,
                OuterConeCos = light.OuterConeCos,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            });
        }
        if (_gpuLights.Count == 0)
            _gpuLights.Add(default);

        int generation = RequireReadbackWriteGeneration();
        int lightByteCount = checked(_gpuLights.Count * Unsafe.SizeOf<ClusterGpuLight>());
        EnsureLightValueBuffer(generation, lightByteCount);
        Buffer lightBuffer = _lightBuffers[generation]
            ?? throw new InvalidOperationException("The admitted Cluster light buffer was not created.");
        WriteMappedBuffer(
            lightBuffer,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_gpuLights)));

        EnsureLightStructureResources(
            frame.Width,
            frame.Height,
            source.Directional.Count,
            source.Points.Count,
            source.Spots.Count);

        frame.LightBuffer = ImportBuffer(
            ref graph,
            lightBuffer,
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        frame.LightCounts = ImportBuffer(
            ref graph,
            _lightCountsBuffer
                ?? throw new InvalidOperationException("The Cluster light-count buffer was not created."),
            PipelineSync.AllShading,
            ResourceAccess.ConstantBuffer,
            ResourceContentState.Defined);
        frame.LightGrid = ImportBuffer(
            ref graph,
            _lightGridBuffer
                ?? throw new InvalidOperationException("The Cluster light-grid buffer was not created."),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        frame.LightIndices = ImportBuffer(
            ref graph,
            _lightIndicesBuffer
                ?? throw new InvalidOperationException("The Cluster light-index buffer was not created."),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        frame.LightGridUniforms = ImportBuffer(
            ref graph,
            _lightGridUniformsBuffer
                ?? throw new InvalidOperationException("The Cluster light-grid uniform buffer was not created."),
            PipelineSync.AllShading,
            ResourceAccess.ConstantBuffer,
            ResourceContentState.Defined);
    }

    private void EnsureLightValueBuffer(int generation, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)generation, 1u);
        if (byteCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        if (_lightBuffers[generation] is not null
            && _lightBufferCapacities[generation] >= byteCount)
        {
            return;
        }

        Buffer replacement = _backend.CreateBuffer(
            _device,
            new BufferDesc(
                checked((ulong)byteCount),
                BufferUsages.ShaderRead,
                $"Cluster lights {generation}"),
            MemoryType.Upload);
        Buffer? previous = _lightBuffers[generation];
        _lightBuffers[generation] = replacement;
        _lightBufferCapacities[generation] = byteCount;
        List<Exception>? failures = null;
        Dispose(ref previous, ref failures);
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void EnsureLightStructureResources(
        int width,
        int height,
        int directionalCount,
        int pointCount,
        int spotCount)
    {
        if (_lightCountsBuffer is not null
            && _lightGridBuffer is not null
            && _lightIndicesBuffer is not null
            && _lightGridUniformsBuffer is not null
            && _lightStructureWidth == width
            && _lightStructureHeight == height
            && _lightStructureDirectionalCount == directionalCount
            && _lightStructurePointCount == pointCount
            && _lightStructureSpotCount == spotCount)
        {
            return;
        }

        uint tileCountX = checked((uint)((width + LightTileSize - 1) / LightTileSize));
        uint tileCountY = checked((uint)((height + LightTileSize - 1) / LightTileSize));
        uint cellCount = checked(tileCountX * tileCountY * _options.LightDepthSlices);
        uint nonDirectional = checked((uint)(pointCount + spotCount));
        var cells = new Vector2UInt[checked((int)cellCount)];
        cells.AsSpan().Fill(new Vector2UInt(0, nonDirectional));
        uint[] indices = new uint[Math.Max(1, checked((int)nonDirectional))];
        for (int index = 0; index < nonDirectional; index++)
            indices[index] = checked((uint)directionalCount + (uint)index);

        ClusterLightCounts counts = new()
        {
            DirectionalCount = checked((uint)directionalCount),
            PointCount = checked((uint)pointCount),
            SpotCount = checked((uint)spotCount),
        };
        ClusterLightGridUniforms uniforms = new()
        {
            TileSizeX = LightTileSize,
            TileSizeY = LightTileSize,
            TileCountX = tileCountX,
            TileCountY = tileCountY,
            ZParams = new Vector4(0.1f, 1000.0f, 1.0f / 999.9f, 0.0f),
            DepthSliceCount = _options.LightDepthSlices,
        };

        Buffer? nextCounts = null;
        Buffer? nextGrid = null;
        Buffer? nextIndices = null;
        Buffer? nextUniforms = null;
        try
        {
            nextCounts = CreateLightUploadBuffer(
                256,
                BufferUsages.Constant,
                "Cluster light counts",
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref counts, 1)));
            nextGrid = CreateLightUploadBuffer(
                checked((ulong)cells.Length * (ulong)Unsafe.SizeOf<Vector2UInt>()),
                BufferUsages.ShaderRead,
                "Cluster light grid",
                MemoryMarshal.AsBytes(cells.AsSpan()));
            nextIndices = CreateLightUploadBuffer(
                checked((ulong)indices.Length * sizeof(uint)),
                BufferUsages.ShaderRead,
                "Cluster light indices",
                MemoryMarshal.AsBytes(indices.AsSpan()));
            nextUniforms = CreateLightUploadBuffer(
                256,
                BufferUsages.Constant,
                "Cluster light grid uniforms",
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1)));
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            Dispose(ref nextUniforms, ref cleanupFailures);
            Dispose(ref nextIndices, ref cleanupFailures);
            Dispose(ref nextGrid, ref cleanupFailures);
            Dispose(ref nextCounts, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster light-structure creation failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }

        Buffer? previousCounts = _lightCountsBuffer;
        Buffer? previousGrid = _lightGridBuffer;
        Buffer? previousIndices = _lightIndicesBuffer;
        Buffer? previousUniforms = _lightGridUniformsBuffer;
        _lightCountsBuffer = nextCounts;
        _lightGridBuffer = nextGrid;
        _lightIndicesBuffer = nextIndices;
        _lightGridUniformsBuffer = nextUniforms;
        _lightStructureWidth = width;
        _lightStructureHeight = height;
        _lightStructureDirectionalCount = directionalCount;
        _lightStructurePointCount = pointCount;
        _lightStructureSpotCount = spotCount;

        List<Exception>? releaseFailures = null;
        Dispose(ref previousUniforms, ref releaseFailures);
        Dispose(ref previousIndices, ref releaseFailures);
        Dispose(ref previousGrid, ref releaseFailures);
        Dispose(ref previousCounts, ref releaseFailures);
        if (releaseFailures is not null)
        {
            throw releaseFailures.Count == 1
                ? releaseFailures[0]
                : new AggregateException(releaseFailures);
        }
    }

    private Buffer CreateLightUploadBuffer(
        ulong byteCount,
        BufferUsages usage,
        string name,
        ReadOnlySpan<byte> contents)
    {
        Buffer result = _backend.CreateBuffer(
            _device,
            new BufferDesc(byteCount, usage, name),
            MemoryType.Upload);
        try
        {
            WriteMappedBuffer(result, contents);
            return result;
        }
        catch (Exception primary)
        {
            Buffer? cleanup = result;
            List<Exception>? cleanupFailures = null;
            Dispose(ref cleanup, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Cluster light buffer '{name}' creation failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private void WriteMappedBuffer(Buffer destination, ReadOnlySpan<byte> contents)
    {
        BufferRange range = new(0, checked((ulong)contents.Length));
        using MappedBuffer mapping = _backend.Map(destination, MapType.Write, range);
        contents.CopyTo(mapping.Bytes);
        mapping.Flush(range);
    }

    private static void RecordBufferInitialization(
        ref RenderGraphFrame graph,
        in FrameResources frame)
    {
        ReadOnlySpan<GraphBufferId> buffers = frame.ClearBuffers;
        for (int index = 0; index < buffers.Length; index++)
        {
            ClusterBufferClearParameters passData = new(buffers[index]);
            _ = graph.AddCopyPass(
                $"Initialize Cluster frame buffer {index}",
                PassQueueSelection.AnyOfType(QueueType.Copy),
                passData,
                default,
                static (ref PassDefinition access, ref ClusterBufferClearParameters data) =>
                    _ = access.Write(
                        data.Buffer,
                        BufferRange.Whole,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                ClusterBufferClearParameters.Record);
        }

        ClusterBufferCopyParameters copy = new(
            frame.CandidateArgsInitialization,
            frame.CandidateArgs,
            16);
        _ = graph.AddCopyPass(
            "Initialize Cluster candidate dispatch arguments",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            copy,
            default,
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                _ = access.Read(
                    data.Source,
                    new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource);
                _ = access.Write(
                    data.Destination,
                    new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterBufferCopyParameters.Record);
    }

    private static void RecordTargetInitialization(
        ref RenderGraphFrame graph,
        in FrameResources frame)
    {
        GraphColorAttachmentViewId vis = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            label: "Cluster visibility clear view");
        GraphColorAttachmentViewId softwareDepth = graph.CreateColorAttachmentView(
            frame.SoftwareDepth,
            label: "Cluster software depth clear view");
        GraphColorAttachmentViewId scene = graph.CreateColorAttachmentView(
            frame.SceneColor,
            label: "Cluster scene clear view");
        GraphColorAttachmentViewId motion = graph.CreateColorAttachmentView(
            frame.MotionVectors,
            label: "Cluster motion clear view");
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            label: "Cluster depth clear view");
        ClusterTargetClearParameters data = new(
            vis,
            softwareDepth,
            scene,
            motion,
            depth);
        _ = graph.AddRasterPass(
            "Clear Cluster frame targets",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            default,
            ClusterTargetClearParameters.Declare,
            ClusterTargetClearParameters.Record);
    }

    private void AuthorHistoryInitialization(
        ref RenderGraphFrame graph,
        in FrameResources frame)
    {
        for (int mip = 0; mip < _history!.HiZMipCount; mip++)
        {
            GraphColorAttachmentViewId view = graph.CreateColorAttachmentView(
                frame.PreviousHiZ,
                new TextureSubresourceRange(
                    checked((uint)mip),
                    1,
                    0,
                    1,
                    TextureAspects.Color),
                label: $"Cluster HiZ history initialization mip {mip}");
            ClusterColorAttachmentClearParameters data = new(view, Vector4.Zero);
            _ = graph.AddRasterPass(
                $"Initialize Cluster HiZ history mip {mip}",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                data,
                default,
                ClusterColorAttachmentClearParameters.Declare,
                ClusterColorAttachmentClearParameters.Record);
        }

        GraphColorAttachmentViewId scene = graph.CreateColorAttachmentView(
            frame.PreviousScene,
            label: "Cluster scene history initialization");
        ClusterColorAttachmentClearParameters sceneData = new(
            scene,
            new Vector4(0, 0, 0, 1));
        _ = graph.AddRasterPass(
            "Initialize Cluster scene history",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            sceneData,
            default,
            ClusterColorAttachmentClearParameters.Declare,
            ClusterColorAttachmentClearParameters.Record);

        GraphColorAttachmentViewId motion = graph.CreateColorAttachmentView(
            frame.PreviousMotion,
            label: "Cluster motion history initialization");
        ClusterColorAttachmentClearParameters motionData = new(motion, Vector4.Zero);
        _ = graph.AddRasterPass(
            "Initialize Cluster motion history",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            motionData,
            default,
            ClusterColorAttachmentClearParameters.Declare,
            ClusterColorAttachmentClearParameters.Record);

        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(
            frame.PreviousDepth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            label: "Cluster depth history initialization");
        ClusterDepthAttachmentClearParameters depthData = new(depth);
        _ = graph.AddRasterPass(
            "Initialize Cluster depth history",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            depthData,
            default,
            ClusterDepthAttachmentClearParameters.Declare,
            ClusterDepthAttachmentClearParameters.Record);
    }

    private static GraphBufferId UploadUniform<T>(
        ref RenderGraphFrame graph,
        T value,
        string name)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        int byteSize = checked((valueSize + 255) & ~255);
        Span<byte> bytes = stackalloc byte[byteSize];
        bytes.Clear();
        MemoryMarshal.Write(bytes, in value);
        return UploadBytes(ref graph, bytes, BufferUsages.Constant, name);
    }

    private static GraphBufferId UploadStructs<T>(
        ref RenderGraphFrame graph,
        scoped ReadOnlySpan<T> values,
        BufferUsages usage,
        string name)
        where T : unmanaged
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one value.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private static GraphBufferId UploadWords(
        ref RenderGraphFrame graph,
        scoped ReadOnlySpan<uint> values,
        BufferUsages usage,
        string name)
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one word.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private static GraphBufferId UploadBytes(
        ref RenderGraphFrame graph,
        scoped ReadOnlySpan<byte> bytes,
        BufferUsages usage,
        string name)
    {
        return graph.Upload(bytes, usage, name);
    }

    private static GraphBufferId ImportBuffer(
        ref RenderGraphFrame graph,
        Buffer buffer,
        PipelineSync sync,
        ResourceAccess access,
        ResourceContentState contents,
        ReadOnlySpan<QueueCompletion> readiness = default)
    {
        BufferRange range = new(0, buffer.Info.Size);
        if (readiness.IsEmpty)
        {
            return graph.Import(
                buffer,
                [new BufferBoundaryState(range, sync, access, contents)]);
        }

        var endpoints = new BufferBoundaryState[readiness.Length];
        for (int index = 0; index < endpoints.Length; index++)
        {
            QueueCompletion completion = readiness[index];
            endpoints[index] = new BufferBoundaryState(
                range,
                sync,
                access,
                contents,
                completion.Queue,
                completion);
        }
        return graph.Import(buffer, endpoints);
    }

    private static GraphTextureId ImportTexture(
        ref RenderGraphFrame graph,
        Texture texture,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        ResourceContentState contents)
    {
        TextureInfo info = texture.Info;
        TextureAspects aspects = info.Format switch
        {
            Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
            Format.D24UNormS8UInt or Format.D32FloatS8UInt =>
                TextureAspects.Depth | TextureAspects.Stencil,
            _ => TextureAspects.Color,
        };
        TextureSubresourceRange range = new(
            0,
            info.MipLevelCount,
            0,
            info.ArrayLayerCount,
            aspects);
        return graph.Import(
            texture,
            [new TextureBoundaryState(range, sync, access, layout, contents)]);
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
    private readonly record struct Vector2UInt(uint X, uint Y);

    private readonly record struct MaterialResources(
        ClusterMaterialGpuBinding binding,
        GraphBufferId scalars,
        GraphTextureId albedo,
        GraphTextureId normal,
        GraphTextureId arm)
    {
        internal ClusterMaterialGpuBinding Binding { get; } = binding;
        internal GraphBufferId Scalars { get; } = scalars;
        internal GraphTextureId Albedo { get; } = albedo;
        internal GraphTextureId Normal { get; } = normal;
        internal GraphTextureId Arm { get; } = arm;
    }

    private struct FrameResources
    {
        private readonly FrameResourceScratch _scratch;

        internal FrameResources(FrameResourceScratch scratch)
            => _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));

        internal int Width;
        internal int Height;
        internal uint MaterialCount;
        internal uint SlotCapacity;
        internal int PageFaultCapacity;
        internal GraphBufferId PageHeap;
        internal GraphBufferId Bvh;
        internal GraphBufferId InstanceData;
        internal GraphBufferId InstanceProperties;
        internal BufferRange InstancePropertiesRange;
        internal GraphBufferId PageFaultReadback;
        internal GraphBufferId FrameMetricsReadback;
        internal GraphTextureId Target;
        internal GraphTextureId PreviousHiZ;
        internal GraphTextureId CurrentHiZ;
        internal GraphTextureId PreviousScene;
        internal GraphTextureId CurrentSceneHistory;
        internal GraphTextureId PreviousMotion;
        internal GraphTextureId CurrentMotionHistory;
        internal GraphTextureId PreviousDepth;
        internal GraphTextureId CurrentDepthHistory;
        internal GraphBufferId SlotBuffer;
        internal GraphBufferId ReadOffsetZero;
        internal GraphBufferId CandidateArgs;
        internal GraphBufferId CandidateArgsInitialization;
        internal GraphBufferId CandidateClusters;
        internal GraphBufferId CandidateCount;
        internal GraphBufferId DrawArgs;
        internal GraphBufferId PageFaults;
        internal GraphBufferId Phase2CandidateArgs;
        internal GraphBufferId Phase2CandidateClusters;
        internal GraphBufferId Phase2CandidateCount;
        internal GraphBufferId Phase2DrawArgs;
        internal GraphBufferId VisibleClusters;
        internal GraphBufferId RasterBinMeta;
        internal GraphBufferId BinnedClusters;
        internal GraphBufferId BinnedDrawArgs;
        internal GraphBufferId BinnedHardwareDrawArgs;
        internal GraphBufferId HardwareIndirectArgs;
        internal GraphBufferId BinningDispatchArgs;
        internal GraphBufferId SoftwareDispatchArgs;
        internal GraphBufferId RasterReserveCounters;
        internal GraphBufferId DeformBinMeta;
        internal GraphBufferId DeformBinnedClusters;
        internal GraphBufferId DeformDispatchArgs;
        internal GraphBufferId DeformReserveCounters;
        internal GraphBufferId CacheOffsets;
        internal GraphBufferId CacheAllocationCounter;
        internal GraphBufferId DeformCache;
        internal GraphBufferId SoftwareDebug;
        internal GraphBufferId ShadeBinCounts;
        internal GraphBufferId ShadeBinOffsets;
        internal GraphBufferId ShadeIndirectArgs;
        internal GraphBufferId ShadeScatterCounts;
        internal GraphBufferId ShadeReserveCounters;
        internal GraphBufferId PixelCoordinates;
        internal GraphBufferId LightCounts;
        internal GraphBufferId LightBuffer;
        internal GraphBufferId LightGridUniforms;
        internal GraphBufferId LightGrid;
        internal GraphBufferId LightIndices;
        internal GraphTextureId CookieAtlas;
        internal GraphTextureId VisBuffer;
        internal GraphTextureId Depth;
        internal GraphTextureId SoftwareDepth;
        internal GraphTextureId SceneColor;
        internal GraphTextureId MotionVectors;
        internal GraphTextureId TemporalColor;
        internal readonly ReadOnlySpan<GraphBufferId> ClearBuffers => _scratch.ClearBuffers;
        internal readonly ReadOnlySpan<MaterialResources> Materials => _scratch.Materials;

        internal GraphBufferId AddBuffer(
            ref RenderGraphFrame graph,
            ulong size,
            BufferUsages additional,
            string name)
        {
            _scratch.EnsureCanAddClearBuffer();
            GraphBufferId result = graph.CreateBuffer(new BufferDesc(
                Math.Max(size, 4),
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination |
                additional,
                name));
            _scratch.AddClearBuffer(result);
            return result;
        }

        internal void AddMaterial(in MaterialResources material)
            => _scratch.AddMaterial(material);

        internal readonly bool TryGetImportedTexture(
            Texture texture,
            out GraphTextureId imported)
            => _scratch.TryGetImportedTexture(texture, out imported);

        internal readonly void AddImportedTexture(Texture texture, GraphTextureId imported)
            => _scratch.AddImportedTexture(texture, imported);
    }

    /// <summary>Reusable owner for the variable-length scratch used while authoring one frame.</summary>
    private sealed class FrameResourceScratch
    {
        private const int ClearBufferCapacity = 64;

        private readonly GraphBufferId[] _clearBuffers = new GraphBufferId[ClearBufferCapacity];
        private MaterialResources[] _materials = [];
        private Texture[] _importedTextures = [];
        private GraphTextureId[] _importedTextureIds = [];
        private int _clearBufferCount;
        private int _materialCount;
        private int _importedTextureCount;
        private bool _active;

        internal ReadOnlySpan<GraphBufferId> ClearBuffers
        {
            get
            {
                EnsureActive();
                return _clearBuffers.AsSpan(0, _clearBufferCount);
            }
        }

        internal ReadOnlySpan<MaterialResources> Materials
        {
            get
            {
                EnsureActive();
                return _materials.AsSpan(0, _materialCount);
            }
        }

        internal void Begin(int materialCapacity)
        {
            if (_active)
                throw new InvalidOperationException("Cluster frame-resource scratch is already active.");
            ArgumentOutOfRangeException.ThrowIfNegative(materialCapacity);
            if (_materials.Length < materialCapacity)
                Array.Resize(ref _materials, materialCapacity);
            int textureCapacity = checked(materialCapacity * 3);
            if (_importedTextures.Length < textureCapacity)
                Array.Resize(ref _importedTextures, textureCapacity);
            if (_importedTextureIds.Length < textureCapacity)
                Array.Resize(ref _importedTextureIds, textureCapacity);
            _active = true;
        }

        internal void End()
        {
            EnsureActive();
            _materials.AsSpan(0, _materialCount).Clear();
            _importedTextures.AsSpan(0, _importedTextureCount).Clear();
            _clearBufferCount = 0;
            _materialCount = 0;
            _importedTextureCount = 0;
            _active = false;
        }

        internal void EnsureCanAddClearBuffer()
        {
            EnsureActive();
            if ((uint)_clearBufferCount >= (uint)_clearBuffers.Length)
            {
                throw new InvalidOperationException(
                    "The Cluster frame clear-buffer capacity is exhausted.");
            }
        }

        internal void AddClearBuffer(GraphBufferId buffer)
            => _clearBuffers[_clearBufferCount++] = buffer;

        internal void AddMaterial(in MaterialResources material)
        {
            EnsureActive();
            _materials[_materialCount++] = material;
        }

        internal bool TryGetImportedTexture(Texture texture, out GraphTextureId imported)
        {
            EnsureActive();
            for (int index = 0; index < _importedTextureCount; index++)
            {
                if (!ReferenceEquals(_importedTextures[index], texture))
                    continue;
                imported = _importedTextureIds[index];
                return true;
            }
            imported = default;
            return false;
        }

        internal void AddImportedTexture(Texture texture, GraphTextureId imported)
        {
            EnsureActive();
            _importedTextures[_importedTextureCount] = texture;
            _importedTextureIds[_importedTextureCount] = imported;
            _importedTextureCount++;
        }

        private void EnsureActive()
        {
            if (!_active)
                throw new InvalidOperationException("Cluster frame-resource scratch is not active.");
        }
    }
}

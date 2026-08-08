using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterRendererSystem
{
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
    internal const int DiagnosticsReadbackByteSize = 92;
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
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
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

        var frame = new FrameResources(checked((int)snapshot.MaterialCount))
        {
            Width = width,
            Height = height,
            MaterialCount = materialCount,
            SlotCapacity = snapshot.SlotCapacity,
            PageFaultCapacity = binding.PageFaultCapacity,
            PageHeap = graph.Import(
                binding.PageHeap,
                GraphResourceUsage.ShaderResource,
                GraphResourceUsage.ShaderResource),
            Bvh = graph.Import(
                binding.Bvh,
                GraphResourceUsage.ShaderResource,
                GraphResourceUsage.ShaderResource),
            InstanceData = graph.Import(
                binding.PropertyData,
                GraphResourceUsage.ShaderResource,
                GraphResourceUsage.ShaderResource),
            InstanceProperties = graph.Import(
                binding.InstancePropertyMetadata,
                GraphResourceUsage.VertexOrConstantBuffer,
                GraphResourceUsage.VertexOrConstantBuffer),
            InstancePropertiesRange = binding.InstancePropertyMetadataRange,
            PageFaultReadback = graph.Import(
                _pageFaultReadbacks[RequireReadbackWriteGeneration()]
                    ?? throw new InvalidOperationException("Cluster page-fault readback was not created."),
                GraphResourceUsage.CopyDestination,
                GraphResourceUsage.CopyDestination,
                readiness: _readbackFences[RequireReadbackWriteGeneration()],
                contentsAvailable: false),
            Target = graph.GetImported(target.Texture),
            PreviousHiZ = graph.Import(
                _history!.PreviousHiZ,
                _history.PreviousHiZState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.PreviousReadiness,
                contentsAvailable: _history.PreviousContentsAvailable),
            CurrentHiZ = graph.Import(
                _history.CurrentHiZ,
                _history.CurrentHiZState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.CurrentReadiness,
                contentsAvailable: _history.CurrentContentsAvailable),
            PreviousScene = graph.Import(
                _history.PreviousScene,
                _history.PreviousSceneState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.PreviousReadiness,
                contentsAvailable: _history.PreviousContentsAvailable),
            CurrentSceneHistory = graph.Import(
                _history.CurrentScene,
                _history.CurrentSceneState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.CurrentReadiness,
                contentsAvailable: _history.CurrentContentsAvailable),
            PreviousMotion = graph.Import(
                _history.PreviousMotion,
                _history.PreviousMotionState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.PreviousReadiness,
                contentsAvailable: _history.PreviousContentsAvailable),
            CurrentMotionHistory = graph.Import(
                _history.CurrentMotion,
                _history.CurrentMotionState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.CurrentReadiness,
                contentsAvailable: _history.CurrentContentsAvailable),
            PreviousDepth = graph.Import(
                _history.PreviousDepth,
                _history.PreviousDepthState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.PreviousReadiness,
                contentsAvailable: _history.PreviousContentsAvailable),
            CurrentDepthHistory = graph.Import(
                _history.CurrentDepth,
                _history.CurrentDepthState,
                GraphResourceUsage.ShaderResource,
                readiness: _history.CurrentReadiness,
                contentsAvailable: _history.CurrentContentsAvailable),
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
        if (_options.EnableDiagnosticsReadback)
        {
            frame.DiagnosticsReadback = graph.Import(
                _diagnosticsReadbacks[RequireReadbackWriteGeneration()]
                    ?? throw new InvalidOperationException("Cluster diagnostics readback was not created."),
                GraphResourceUsage.CopyDestination,
                GraphResourceUsage.CopyDestination,
                readiness: _readbackFences[RequireReadbackWriteGeneration()],
                contentsAvailable: false);
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
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
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
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        ref FrameResources frame)
    {
        SmallList<Texture> textures = default;
        SmallList<TextureHandle> textureIds = default;
        foreach (ClusterMaterialState material in _materials!.States)
        {
            TextureHandle albedo = ImportMaterialTexture(
                ref graph,
                ref textures,
                ref textureIds,
                material,
                "AlbedoMap");
            TextureHandle normal = material.ParameterKind == ClusterMaterialParameterKind.StandardPbr
                ? ImportMaterialTexture(
                    ref graph,
                    ref textures,
                    ref textureIds,
                    material,
                    "NormalMap")
                : default;
            TextureHandle arm = material.ParameterKind == ClusterMaterialParameterKind.StandardPbr
                ? ImportMaterialTexture(
                    ref graph,
                    ref textures,
                    ref textureIds,
                    material,
                    "ARMMap")
                : default;
            frame.AddMaterial(new MaterialResources(
                material,
                graph.Import(material.Scalars, GraphResourceUsage.ShaderResource, GraphResourceUsage.ShaderResource),
                albedo,
                normal,
                arm));
        }
        frame.CookieAtlas = graph.Import(
            _materials.CookieAtlas,
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
    }

    private static TextureHandle ImportMaterialTexture(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        ref SmallList<Texture> imported,
        ref SmallList<TextureHandle> importedIds,
        ClusterMaterialState material,
        string name)
    {
        if (!material.Textures.TryGetValue(name, out Texture? handle) || handle is null)
            throw new InvalidOperationException($"Cluster material '{material.Name}' has no '{name}' texture.");
        for (int index = 0; index < imported.Count; index++)
        {
            if (ReferenceEquals(imported[index], handle))
                return importedIds[index];
        }

        TextureHandle logical = graph.Import(
            handle,
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
        imported.Add(handle);
        importedIds.Add(logical);
        return logical;
    }

    private void CreateLightResources(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
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

        frame.LightBuffer = graph.Import(
            lightBuffer,
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
        frame.LightCounts = graph.Import(
            _lightCountsBuffer
                ?? throw new InvalidOperationException("The Cluster light-count buffer was not created."),
            GraphResourceUsage.VertexOrConstantBuffer,
            GraphResourceUsage.VertexOrConstantBuffer);
        frame.LightGrid = graph.Import(
            _lightGridBuffer
                ?? throw new InvalidOperationException("The Cluster light-grid buffer was not created."),
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
        frame.LightIndices = graph.Import(
            _lightIndicesBuffer
                ?? throw new InvalidOperationException("The Cluster light-index buffer was not created."),
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
        frame.LightGridUniforms = graph.Import(
            _lightGridUniformsBuffer
                ?? throw new InvalidOperationException("The Cluster light-grid uniform buffer was not created."),
            GraphResourceUsage.VertexOrConstantBuffer,
            GraphResourceUsage.VertexOrConstantBuffer);
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

        _lightBuffers[generation]?.Dispose();
        _lightBuffers[generation] = _backend.CreateBuffer(
            _device,
            new BufferDesc(
                checked((ulong)byteCount),
                BufferUsages.ShaderRead,
                $"Cluster lights {generation}"),
            MemoryType.Upload);
        _lightBufferCapacities[generation] = byteCount;
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

        _lightCountsBuffer?.Dispose();
        _lightGridBuffer?.Dispose();
        _lightIndicesBuffer?.Dispose();
        _lightGridUniformsBuffer?.Dispose();
        _lightCountsBuffer = null;
        _lightGridBuffer = null;
        _lightIndicesBuffer = null;
        _lightGridUniformsBuffer = null;

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

        _lightCountsBuffer = CreateLightUploadBuffer(
            256,
            BufferUsages.Constant,
            "Cluster light counts",
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref counts, 1)));
        _lightGridBuffer = CreateLightUploadBuffer(
            checked((ulong)cells.Length * (ulong)Unsafe.SizeOf<Vector2UInt>()),
            BufferUsages.ShaderRead,
            "Cluster light grid",
            MemoryMarshal.AsBytes(cells.AsSpan()));
        _lightIndicesBuffer = CreateLightUploadBuffer(
            checked((ulong)indices.Length * sizeof(uint)),
            BufferUsages.ShaderRead,
            "Cluster light indices",
            MemoryMarshal.AsBytes(indices.AsSpan()));
        _lightGridUniformsBuffer = CreateLightUploadBuffer(
            256,
            BufferUsages.Constant,
            "Cluster light grid uniforms",
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1)));

        _lightStructureWidth = width;
        _lightStructureHeight = height;
        _lightStructureDirectionalCount = directionalCount;
        _lightStructurePointCount = pointCount;
        _lightStructureSpotCount = spotCount;
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
        catch
        {
            result.Dispose();
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
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame)
    {
        using (IUnsafeRenderGraphBuilder builder =
               graph.AddUnsafePass<ClusterClearBuffersPassData>(
                   "Initialize Cluster frame buffers",
                   out ClusterClearBuffersPassData passData))
        {
            passData.Buffers = new BufferHandle[frame.ClearBuffers.Length];
            for (int index = 0; index < passData.Buffers.Length; index++)
            {
                BufferHandle buffer = frame.ClearBuffers[index];
                passData.Buffers[index] = buffer;
                builder.UseBuffer(
                    buffer,
                    GraphResourceUsage.UnorderedAccess,
                    GraphAccess.WriteAll);
            }
            builder.SetRenderFunc<ClusterClearBuffersPassData>(
                static (data, context) =>
                {
                    foreach (BufferHandle buffer in data.Buffers)
                        context.FillBuffer(buffer);
                });
        }

        using (IUnsafeRenderGraphBuilder builder =
               graph.AddUnsafePass<ClusterBufferCopyPassData>(
                   "Initialize Cluster candidate dispatch arguments",
                   out ClusterBufferCopyPassData passData))
        {
            passData.Source = frame.CandidateArgsInitialization;
            passData.Destination = frame.CandidateArgs;
            passData.ByteCount = 16;
            builder.UseBuffer(
                passData.Source,
                GraphResourceUsage.CopySource,
                GraphAccess.Read);
            builder.UseBuffer(
                passData.Destination,
                GraphResourceUsage.CopyDestination,
                GraphAccess.WriteAll);
            builder.SetRenderFunc<ClusterBufferCopyPassData>(
                static (data, context) =>
                    context.CopyBufferRegion(
                        data.Source,
                        0,
                        data.Destination,
                        0,
                        data.ByteCount));
        }
    }

    private static void RecordTargetInitialization(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame)
    {
        TextureViewHandle vis = graph.CreateTextureView(
            frame.VisBuffer,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster visibility clear view");
        TextureViewHandle softwareDepth = graph.CreateTextureView(
            frame.SoftwareDepth,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster software depth clear view");
        TextureViewHandle scene = graph.CreateTextureView(
            frame.SceneColor,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster scene clear view");
        TextureViewHandle motion = graph.CreateTextureView(
            frame.MotionVectors,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster motion clear view");
        TextureViewHandle depth = graph.CreateTextureView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            GraphTextureViewUsage.DepthStencilAttachment,
            name: "Cluster depth clear view");
        using IRasterRenderGraphBuilder builder =
            graph.AddRasterRenderPass<ClusterClearPassData>(
                "Clear Cluster frame targets",
                out _);
        builder.SetRenderAttachment(
            vis,
            0,
            GraphAccess.WriteAll,
            LoadType.Clear,
            Vector4.Zero);
        builder.SetRenderAttachment(
            softwareDepth,
            1,
            GraphAccess.WriteAll,
            LoadType.Clear,
            Vector4.Zero);
        builder.SetRenderAttachment(
            scene,
            2,
            GraphAccess.WriteAll,
            LoadType.Clear,
            new Vector4(0, 0, 0, 1));
        builder.SetRenderAttachment(
            motion,
            3,
            GraphAccess.WriteAll,
            LoadType.Clear,
            Vector4.Zero);
        builder.SetRenderAttachmentDepth(
            depth,
            GraphAccess.WriteAll,
            LoadType.Clear,
            clearDepth: 1.0f);
    }

    private void AuthorHistoryInitialization(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame)
    {
        for (int mip = 0; mip < _history!.HiZMipCount; mip++)
        {
            TextureViewHandle view = graph.CreateTextureView(
                frame.PreviousHiZ,
                new TextureSubresourceRange(
                    checked((uint)mip),
                    1,
                    0,
                    1,
                    TextureAspects.Color),
                GraphTextureViewUsage.ColorAttachment,
                name: $"Cluster HiZ history initialization mip {mip}");
            using IRasterRenderGraphBuilder builder =
                graph.AddRasterRenderPass<ClusterClearPassData>(
                    $"Initialize Cluster HiZ history mip {mip}",
                    out _);
            builder.SetRenderAttachment(
                view,
                0,
                GraphAccess.WriteAll,
                LoadType.Clear,
                Vector4.Zero);
        }

        TextureViewHandle scene = graph.CreateTextureView(
            frame.PreviousScene,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster scene history initialization");
        using (IRasterRenderGraphBuilder builder =
               graph.AddRasterRenderPass<ClusterClearPassData>(
                   "Initialize Cluster scene history",
                   out _))
        {
            builder.SetRenderAttachment(
                scene,
                0,
                GraphAccess.WriteAll,
                LoadType.Clear,
                new Vector4(0, 0, 0, 1));
        }

        TextureViewHandle motion = graph.CreateTextureView(
            frame.PreviousMotion,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster motion history initialization");
        using (IRasterRenderGraphBuilder builder =
               graph.AddRasterRenderPass<ClusterClearPassData>(
                   "Initialize Cluster motion history",
                   out _))
        {
            builder.SetRenderAttachment(
                motion,
                0,
                GraphAccess.WriteAll,
                LoadType.Clear,
                Vector4.Zero);
        }

        TextureViewHandle depth = graph.CreateTextureView(
            frame.PreviousDepth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            GraphTextureViewUsage.DepthStencilAttachment,
            name: "Cluster depth history initialization");
        using (IRasterRenderGraphBuilder builder =
               graph.AddRasterRenderPass<ClusterClearPassData>(
                   "Initialize Cluster depth history",
                   out _))
        {
            builder.SetRenderAttachmentDepth(
                depth,
                GraphAccess.WriteAll,
                LoadType.Clear,
                clearDepth: 1.0f);
        }
    }

    private static BufferHandle UploadUniform<T>(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
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

    private static BufferHandle UploadStructs<T>(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        ReadOnlySpan<T> values,
        BufferUsages usage,
        string name)
        where T : unmanaged
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one value.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private static BufferHandle UploadWords(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        ReadOnlySpan<uint> values,
        BufferUsages usage,
        string name)
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one word.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private static BufferHandle UploadBytes(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        ReadOnlySpan<byte> bytes,
        BufferUsages usage,
        string name)
    {
        BufferHandle buffer = graph.CreateBuffer(
            new BufferDesc(checked((ulong)bytes.Length), usage, name),
            MemoryType.Upload);
        graph.InitializeUploadBuffer(buffer, bytes);
        return buffer;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
    private readonly record struct Vector2UInt(uint X, uint Y);

    private readonly record struct MaterialResources(
        ClusterMaterialState state,
        BufferHandle scalars,
        TextureHandle albedo,
        TextureHandle normal,
        TextureHandle arm)
    {
        internal ClusterMaterialState State { get; } = state;
        internal BufferHandle Scalars { get; } = scalars;
        internal TextureHandle Albedo { get; } = albedo;
        internal TextureHandle Normal { get; } = normal;
        internal TextureHandle Arm { get; } = arm;
    }

    private struct FrameResources : IDisposable
    {
        private BufferHandle[] _clearBuffers = null!;
        private MaterialResources[] _materials = null!;
        private int _clearBufferCount;
        private int _materialCount;

        internal FrameResources(int materialCapacity)
        {
            if (materialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(materialCapacity));
            _clearBuffers = ArrayPool<BufferHandle>.Shared.Rent(64);
            _materials = materialCapacity == 0
                ? []
                : ArrayPool<MaterialResources>.Shared.Rent(materialCapacity);
        }

        internal int Width;
        internal int Height;
        internal uint MaterialCount;
        internal uint SlotCapacity;
        internal int PageFaultCapacity;
        internal BufferHandle PageHeap;
        internal BufferHandle Bvh;
        internal BufferHandle InstanceData;
        internal BufferHandle InstanceProperties;
        internal BufferRange InstancePropertiesRange;
        internal BufferHandle PageFaultReadback;
        internal BufferHandle DiagnosticsReadback;
        internal TextureHandle Target;
        internal TextureHandle PreviousHiZ;
        internal TextureHandle CurrentHiZ;
        internal TextureHandle PreviousScene;
        internal TextureHandle CurrentSceneHistory;
        internal TextureHandle PreviousMotion;
        internal TextureHandle CurrentMotionHistory;
        internal TextureHandle PreviousDepth;
        internal TextureHandle CurrentDepthHistory;
        internal BufferHandle SlotBuffer;
        internal BufferHandle ReadOffsetZero;
        internal BufferHandle CandidateArgs;
        internal BufferHandle CandidateArgsInitialization;
        internal BufferHandle CandidateClusters;
        internal BufferHandle CandidateCount;
        internal BufferHandle DrawArgs;
        internal BufferHandle PageFaults;
        internal BufferHandle Phase2CandidateArgs;
        internal BufferHandle Phase2CandidateClusters;
        internal BufferHandle Phase2CandidateCount;
        internal BufferHandle Phase2DrawArgs;
        internal BufferHandle VisibleClusters;
        internal BufferHandle RasterBinMeta;
        internal BufferHandle BinnedClusters;
        internal BufferHandle BinnedDrawArgs;
        internal BufferHandle BinnedHardwareDrawArgs;
        internal BufferHandle HardwareIndirectArgs;
        internal BufferHandle BinningDispatchArgs;
        internal BufferHandle SoftwareDispatchArgs;
        internal BufferHandle RasterReserveCounters;
        internal BufferHandle DeformBinMeta;
        internal BufferHandle DeformBinnedClusters;
        internal BufferHandle DeformDispatchArgs;
        internal BufferHandle DeformReserveCounters;
        internal BufferHandle CacheOffsets;
        internal BufferHandle CacheAllocationCounter;
        internal BufferHandle DeformCache;
        internal BufferHandle SoftwareDebug;
        internal BufferHandle ShadeBinCounts;
        internal BufferHandle ShadeBinOffsets;
        internal BufferHandle ShadeIndirectArgs;
        internal BufferHandle ShadeScatterCounts;
        internal BufferHandle ShadeReserveCounters;
        internal BufferHandle PixelCoordinates;
        internal BufferHandle LightCounts;
        internal BufferHandle LightBuffer;
        internal BufferHandle LightGridUniforms;
        internal BufferHandle LightGrid;
        internal BufferHandle LightIndices;
        internal TextureHandle CookieAtlas;
        internal TextureHandle VisBuffer;
        internal TextureHandle Depth;
        internal TextureHandle SoftwareDepth;
        internal TextureHandle SceneColor;
        internal TextureHandle MotionVectors;
        internal TextureHandle TemporalColor;
        internal readonly ReadOnlySpan<BufferHandle> ClearBuffers =>
            _clearBuffers.AsSpan(0, _clearBufferCount);
        internal readonly ReadOnlySpan<MaterialResources> Materials =>
            _materials.AsSpan(0, _materialCount);

        internal BufferHandle AddBuffer(
            ref global::SomeEngine.RenderGraph.RenderGraph graph,
            ulong size,
            BufferUsages additional,
            string name)
        {
            BufferHandle result = graph.CreateBuffer(new BufferDesc(
                Math.Max(size, 4),
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination |
                additional,
                name));
            if ((uint)_clearBufferCount >= (uint)_clearBuffers.Length)
                throw new InvalidOperationException(
                    "The Cluster frame clear-buffer capacity is exhausted.");
            _clearBuffers[_clearBufferCount++] = result;
            return result;
        }

        internal void AddMaterial(in MaterialResources material)
        {
            if ((uint)_materialCount >= (uint)_materials.Length)
                throw new InvalidOperationException(
                    "The Cluster frame material capacity is exhausted.");
            _materials[_materialCount++] = material;
        }

        public void Dispose()
        {
            BufferHandle[] clearBuffers = _clearBuffers;
            MaterialResources[] materials = _materials;
            _clearBuffers = [];
            _materials = [];
            _clearBufferCount = 0;
            _materialCount = 0;
            ArrayPool<BufferHandle>.Shared.Return(clearBuffers, clearArray: true);
            if (materials.Length != 0)
                ArrayPool<MaterialResources>.Shared.Return(
                    materials,
                    clearArray: true);
        }
    }
}

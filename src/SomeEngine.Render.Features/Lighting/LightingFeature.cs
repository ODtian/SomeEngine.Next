using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Explicit resources produced by the independent clustered-lighting feature.</summary>
public readonly record struct LightingFeatureResources(
    GraphBufferId Grid,
    GraphBufferId LightIndices,
    GraphTextureId CookieAtlas,
    uint CellCountPerView,
    uint MaxLightsPerCell,
    uint ViewCapacity)
{
    public bool IsValid =>
        Grid != default &&
        LightIndices != default &&
        CookieAtlas != default &&
        CellCountPerView != 0 &&
        MaxLightsPerCell != 0 &&
        ViewCapacity != 0;
}

/// <summary>
/// Owns light conversion, fixed-capacity spatial assignment resources, and retained graph passes.
/// Standard shade discovers these resources from its final linked program through the generic
/// Cluster shade-resource boundary.
/// </summary>
public sealed class LightingFeature : IClusterStageResourceProvider, IDisposable
{
    private const uint ThreadCount = 64;
    private readonly LinkedComputePipeline _pipeline;
    private readonly GraphPassId _clearPass;
    private readonly GraphPassId _buildPass;
    private readonly GraphPassId _cookieInitializePass;
    private readonly GraphBufferId _grid;
    private readonly GraphBufferId _lightIndices;
    private readonly GraphTextureId _cookieAtlas;
    private readonly int _width;
    private readonly int _height;
    private readonly uint _viewCapacity;
    private readonly ClusteredLightGridSettings _settings;
    private readonly List<GpuLight> _gpuLights;
    private readonly ClusteredLightGridParameters[] _viewParameters;
    private GraphBufferId _frameLights;
    private GraphBufferId _frameViews;
    private ulong _frameIdentity;
    private bool _disposed;

    public LightingFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputDescription output,
        Shader shader,
        string entryPoint,
        ClusteredLightGridSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Lighting and RenderGraph must use the same Device.", nameof(graph));
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        _width = output.Width;
        _height = output.Height;
        _viewCapacity = output.ViewCapacity;
        _settings = settings ?? new ClusteredLightGridSettings();
        _settings.Validate();
        _gpuLights = new List<GpuLight>(_settings.MaxLights);
        _viewParameters = new ClusteredLightGridParameters[checked((int)_viewCapacity)];

        uint tileCountX = DivideRoundUp(checked((uint)_width), _settings.TileSize);
        uint tileCountY = DivideRoundUp(checked((uint)_height), _settings.TileSize);
        uint cellsPerView = checked(tileCountX * tileCountY * _settings.DepthSliceCount);
        ulong gridBytes = checked((ulong)cellsPerView * _viewCapacity * 2u * sizeof(uint));
        ulong indexBytes = checked(
            (ulong)cellsPerView * _viewCapacity * _settings.MaxLightsPerCell * sizeof(uint));

        LinkedComputePipeline? pipeline = null;
        try
        {
            pipeline = LinkedComputePipeline.Create(
                backend,
                device,
                shader,
                entryPoint,
                "Build clustered lighting grid");
            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                _grid = edit.CreateBuffer(new BufferDesc(
                    gridBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                        BufferUsages.CopyDestination,
                    "Lighting clustered grid"));
                _lightIndices = edit.CreateBuffer(new BufferDesc(
                    indexBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                        BufferUsages.CopyDestination,
                        "Lighting clustered indices"));
                _cookieAtlas = edit.CreatePersistentTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    1,
                    1,
                    1,
                    1,
                    1,
                    1,
                    Format.R8G8B8A8UNorm,
                    TextureUsages.Sampled | TextureUsages.CopyDestination |
                        TextureUsages.ColorAttachment,
                    label: "Lighting cookie atlas"));
                _cookieInitializePass = edit.AddCopyFramePass(
                    "Initialize lighting cookie atlas",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    _cookieAtlas,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref GraphTextureId texture) =>
                        _ = pass.Write(
                            texture,
                            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.Copy,
                            ResourceAccess.CopyDestination,
                            TextureLayout.CopyDestination,
                            WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in GraphTextureId texture) =>
                        commands.ClearTexture(
                            commands.GetTexture(texture),
                            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            System.Numerics.Vector4.One));
                var clear = new LightingClearPassData(_grid, _lightIndices);
                _clearPass = edit.AddCopyFramePass(
                    "Clear lighting assignment",
                    PassQueueSelection.AnyOfType(QueueType.Copy),
                    clear,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    LightingClearPassData.Declare,
                    LightingClearPassData.Record);
                var build = new LightingBuildPassData(default);
                _buildPass = edit.AddComputeFramePass(
                    "Build lighting assignment",
                    PassQueueSelection.AnyOfType(QueueType.Compute),
                    build,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    pipeline.Pipeline,
                    pipeline.ParameterLayout,
                    default,
                    static (ref PassDefinition _, ref LightingBuildPassData _) => { },
                    LightingBuildPassData.Record);
                edit.Commit();
            }
            finally
            {
                edit.Dispose();
            }
            _pipeline = pipeline;
            pipeline = null;
            Resources = new LightingFeatureResources(
                _grid,
                _lightIndices,
                _cookieAtlas,
                cellsPerView,
                _settings.MaxLightsPerCell,
                _viewCapacity);
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    public LightingFeatureResources Resources { get; }

    public void Record(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        ReadOnlySpan<RenderView> views,
        bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (views.IsEmpty || views.Length > _viewParameters.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(views),
                $"Lighting requires 1..{_viewParameters.Length} render views.");
        }

        int sourceLightCount = checked(
            lights.Directional.Count + lights.Points.Count + lights.Spots.Count);
        if (sourceLightCount > _settings.MaxLights)
        {
            throw new InvalidOperationException(
                $"Lighting received {sourceLightCount} lights, exceeding its fixed capacity {_settings.MaxLights}.");
        }
        EncodeLights(lights);
        uint directionalCount = enabled ? checked((uint)lights.Directional.Count) : 0u;
        uint nonDirectionalCount = enabled
            ? checked((uint)(lights.Points.Count + lights.Spots.Count))
            : 0u;
        if (_gpuLights.Count == 0)
            _gpuLights.Add(default);

        Span<ClusteredLightGridParameters> parameters = _viewParameters;
        parameters.Clear();
        for (int index = 0; index < views.Length; index++)
        {
            parameters[index] = ClusteredLightGridParameters.Create(
                in views[index],
                _settings,
                Resources.CellCountPerView,
                directionalCount,
                nonDirectionalCount);
        }

        _frameLights = graph.Upload(
            CollectionsMarshal.AsSpan(_gpuLights),
            BufferUsages.ShaderRead,
            "Lighting frame lights");
        _frameViews = graph.Upload(
            parameters,
            BufferUsages.ShaderRead,
            "Lighting frame views");
        _frameIdentity = graph.FrameIdentity;

        PassDefinition pass = graph.GetPass(_buildPass);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameViews,
                structureStride: ClusteredLightGridParameters.SizeInBytes,
                label: "Lighting view parameters"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameLights,
                structureStride: GpuLight.SizeInBytes,
                label: "Lighting lights"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferUav(
                _grid,
                structureStride: 2 * sizeof(uint),
                label: "Lighting grid output"),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite);
        _ = pass.Bind(
            graph.CreateBufferUav(
                _lightIndices,
                structureStride: sizeof(uint),
                label: "Lighting index output"),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial);
        graph.SetPassData(
            _buildPass,
            new LightingBuildPassData(new DispatchArguments(
                DivideRoundUp(nonDirectionalCount, ThreadCount),
                checked((uint)views.Length),
                1)));
        graph.SetPassEnabled(_clearPass, true);
        graph.SetPassEnabled(_cookieInitializePass, true);
        graph.SetPassEnabled(_buildPass, enabled && nonDirectionalCount != 0);
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out ClusterStageResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binder = name switch
        {
            "LightingViews" when type == ResourceBindingType.BufferSrv => BindLightingViews,
            "LightBuffer" when type == ResourceBindingType.BufferSrv => BindLights,
            "ClusteredLightGrid" when type == ResourceBindingType.BufferSrv => BindGrid,
            "LightIndexList" when type == ResourceBindingType.BufferSrv => BindIndices,
            "LightingCookieAtlas" when type == ResourceBindingType.TextureSrv => BindCookieAtlas,
            _ => null!,
        };
        return binder is not null;
    }

    private void BindLightingViews(ref RenderGraphFrame graph, ref PassDefinition pass)
    {
        RequireRecordedFrame(in graph);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameViews,
                structureStride: ClusteredLightGridParameters.SizeInBytes,
                label: "Standard shade lighting views"),
            PipelineSync.ComputeShading);
    }

    private void BindLights(ref RenderGraphFrame graph, ref PassDefinition pass)
    {
        RequireRecordedFrame(in graph);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameLights,
                structureStride: GpuLight.SizeInBytes,
                label: "Standard shade lights"),
            PipelineSync.ComputeShading);
    }

    private void BindGrid(ref RenderGraphFrame graph, ref PassDefinition pass)
    {
        RequireRecordedFrame(in graph);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _grid,
                structureStride: 2 * sizeof(uint),
                label: "Standard shade clustered grid"),
            PipelineSync.ComputeShading);
    }

    private void BindIndices(ref RenderGraphFrame graph, ref PassDefinition pass)
    {
        RequireRecordedFrame(in graph);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _lightIndices,
                structureStride: sizeof(uint),
                label: "Standard shade light indices"),
            PipelineSync.ComputeShading);
    }

    private void BindCookieAtlas(ref RenderGraphFrame graph, ref PassDefinition pass)
    {
        RequireRecordedFrame(in graph);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _cookieAtlas,
                format: Format.R8G8B8A8UNorm,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Standard shade lighting cookie atlas"),
            PipelineSync.ComputeShading);
    }

    private void RequireRecordedFrame(in RenderGraphFrame graph)
    {
        if (_frameIdentity != graph.FrameIdentity || _frameLights == default || _frameViews == default)
        {
            throw new InvalidOperationException(
                "Lighting must record its frame resources before Standard shade records.");
        }
    }

    private void EncodeLights(RenderLightSet source)
    {
        _gpuLights.Clear();
        for (int index = 0; index < source.Directional.Count; index++)
        {
            RenderDirectionalLight light = source.Directional[index];
            _gpuLights.Add(new GpuLight
            {
                Direction = NormalizeOrZero(light.Direction),
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = Vector4.One,
            });
        }
        for (int index = 0; index < source.Points.Count; index++)
        {
            RenderPointLight light = source.Points[index];
            _gpuLights.Add(new GpuLight
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = Vector4.One,
            });
        }
        for (int index = 0; index < source.Spots.Count; index++)
        {
            RenderSpotLight light = source.Spots[index];
            _gpuLights.Add(new GpuLight
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
                CookieScaleOffset = Vector4.One,
            });
        }
    }

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    private static uint DivideRoundUp(uint value, uint divisor) =>
        value == 0 ? 0 : checked((value + divisor - 1u) / divisor);

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _gpuLights.Clear();
        Array.Clear(_viewParameters);
        _frameLights = default;
        _frameViews = default;
        _frameIdentity = 0;
        _disposed = true;
    }

    private readonly record struct LightingClearPassData(
        GraphBufferId Grid,
        GraphBufferId LightIndices)
    {
        internal static void Declare(
            ref PassDefinition access,
            ref LightingClearPassData data)
        {
            _ = access.Write(
                data.Grid,
                BufferRange.Whole,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete);
            _ = access.Write(
                data.LightIndices,
                BufferRange.Whole,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete);
        }

        internal static void Record(
            ref CopyPassCommandScope commands,
            in LightingClearPassData data)
        {
            Buffer grid = commands.GetBuffer(data.Grid);
            commands.ClearBuffer(grid, new BufferRange(0, grid.Info.Size));
            Buffer indices = commands.GetBuffer(data.LightIndices);
            commands.ClearBuffer(indices, new BufferRange(0, indices.Info.Size));
        }
    }

    private readonly record struct LightingBuildPassData(DispatchArguments Dispatch)
    {
        internal static void Record(
            ref ComputePassCommandScope commands,
            in LightingBuildPassData data) =>
            commands.Dispatch(data.Dispatch);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
internal struct ClusteredLightGridParameters
{
    internal const int SizeInBytes = 144;

    internal Matrix4x4 View;
    internal uint TileSizeX;
    internal uint TileSizeY;
    internal uint TileCountX;
    internal uint TileCountY;
    internal Vector4 ZParams;
    internal uint DepthSliceCount;
    internal uint MaxLightsPerCluster;
    internal uint DirectionalCount;
    internal uint NonDirectionalCount;
    internal float P00;
    internal float P11;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint CellCountPerView;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;

    internal static ClusteredLightGridParameters Create(
        in RenderView view,
        ClusteredLightGridSettings settings,
        uint cellCountPerView,
        uint directionalCount,
        uint nonDirectionalCount)
    {
        const float nearDepth = 0.1f;
        const float farDepth = 10_000.0f;
        float logRange = MathF.Log2(farDepth / nearDepth);
        float sliceScale = settings.DepthSliceCount / logRange;
        return new ClusteredLightGridParameters
        {
            View = view.View,
            TileSizeX = settings.TileSize,
            TileSizeY = settings.TileSize,
            TileCountX = DivideRoundUp(view.ViewportWidth, settings.TileSize),
            TileCountY = DivideRoundUp(view.ViewportHeight, settings.TileSize),
            ZParams = new Vector4(
                nearDepth,
                farDepth,
                sliceScale,
                -MathF.Log2(nearDepth) * sliceScale),
            DepthSliceCount = settings.DepthSliceCount,
            MaxLightsPerCluster = settings.MaxLightsPerCell,
            DirectionalCount = directionalCount,
            NonDirectionalCount = nonDirectionalCount,
            P00 = view.Projection.M11,
            P11 = view.Projection.M22,
            ScreenWidth = view.ViewportWidth,
            ScreenHeight = view.ViewportHeight,
            CellCountPerView = cellCountPerView,
        };
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);
}

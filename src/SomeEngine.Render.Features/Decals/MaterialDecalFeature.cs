using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Decals;

/// <summary>Supplies ordered decal volumes, user properties and independent cell lists to receiving materials.</summary>
public sealed class MaterialDecalFeature : IGraphResourceProvider, IDisposable
{
    private readonly RenderDevice _renderDevice;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly IRenderMaterialResources _materials;
    private readonly MaterialDecalBinning _binning;
    private MaterialDecalSettings _settings;
    private readonly uint _viewCount;
    private readonly GraphBufferId _uniforms;
    private readonly GraphRegionId _region;
    private readonly MaterialDecalData _data;
    private readonly Dictionary<GraphBufferId, DataBuffer> _buffers = [];
    private readonly QueryHandle _decalQuery;
    private readonly RenderWorld _decalWorld;
    private readonly List<MaterialDecalSource> _decalSources = [];
    private readonly Dictionary<(Shader Shader, Type Type), ScalarLayout.ScalarMaterialLayout> _decalLayouts = [];

    public MaterialDecalFeature(RenderDevice renderDevice, IRenderMaterialResources materials, RenderWorld world,
        RenderPrepareSystems prepareSystems, Shader shader, uint width, uint height, uint viewCount,
        MaterialDecalSettings? settings = null, GraphRegionId region = default)
    {
        _renderDevice = renderDevice; _device = renderDevice.Device; _graph = renderDevice.Graph;
        _materials = materials; _settings = settings ?? new(); _viewCount = viewCount;
        _decalWorld = world;
        _decalQuery = world.Query(new QueryDefinitionBuilder().Read<RenderMaterialDecal>().Read<RenderTransform>());
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            GraphRegionId work = edit.AddRegion("Material decal work", region);
            (GraphBufferId volumes, DataBuffer volumeData) = CreateBufferData(ref edit,
                new byte[MaterialDecalVolume.SizeInBytes], "Material decal volumes");
            (GraphBufferId properties, DataBuffer propertyData) = CreateBufferData(ref edit,
                new byte[16], "Material decal properties");
            GraphBufferId uniforms = edit.CreatePersistentBuffer(new(_device.Capabilities.Limits.ConstantBufferAlignment,
                BufferUsages.Constant, "Material decal parameters"), MemoryType.Upload);
            edit.Commit();
            _region = work;
            _uniforms = uniforms;
            _data = new MaterialDecalData(volumes, properties);
            _buffers.Add(volumes, volumeData);
            _buffers.Add(properties, propertyData);
        }
        finally { edit.Dispose(); }
        _binning = new MaterialDecalBinning(_graph, renderDevice.Pipelines, prepareSystems, shader, shader,
            width, height, viewCount, 1, _settings.ReferenceCapacity, BinningSettings(_settings), _region);
    }

    public ImmutableArray<RenderMaterialTextureRead> TextureReads => _data.TextureReads;
    public MaterialDecalSettings Settings => _settings;
    public GraphPassId CompletePass => _binning.CompletePass;
    public GraphRegionId Region => _region;
    public GraphBufferId WorkCounters => _binning.Diagnostics;
    public GraphBufferId VolumeData => _data.Volumes;
    public GraphBufferId PropertyData => _data.Properties;
    public GraphBufferId CellRanges => _binning.Cells;
    public GraphBufferId References => _binning.Indices;
    public uint VolumeCount => (uint)_data.Sources.Length;
    public uint LastFrameOverflowCells => _binning.LastFrameOverflowCells;
    public ulong FeedbackFrameIdentity => _binning.FeedbackFrameIdentity;
    public IEnumerable<BufferDesc> ResourceDescriptions => _buffers.Keys.Append(_uniforms)
        .Select(_graph.GetBufferDescription).Concat(_binning.ResourceDescriptions);

    public void SetSettings(MaterialDecalSettings settings)
    {
        if (_settings == settings) return;
        _binning.SetSettings(BinningSettings(settings), settings.ReferenceCapacity);
        _settings = settings;
        _data.Version++;
    }

    private static MaterialDecalBinningSettings BinningSettings(MaterialDecalSettings settings) => new()
    {
        TileSize = settings.TileSize,
        DepthSliceCount = settings.Index == MaterialDecalIndex.Xyz ? settings.DepthSliceCount : 1u,
        NearDepth = settings.NearDepth,
        FarDepth = settings.FarDepth,
    };

    public void ReplaceShader(Shader previous, Shader next)
    {
        if (_binning.ReplaceShader(previous, next)) _data.Version++;
    }

    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = (name, type) switch
        {
            ("MaterialDecalUniforms", ResourceBindingType.ConstantBuffer) => BindMaterialDecalParameters,
            ("MaterialDecalVolumes", ResourceBindingType.BufferSrv) => BindMaterialDecalVolumes,
            ("MaterialDecalProperties", ResourceBindingType.BufferSrv) => BindMaterialDecalProperties,
            ("MaterialDecalViews", ResourceBindingType.BufferSrv) => BindMaterialDecalViews,
            ("MaterialDecalCells", ResourceBindingType.BufferSrv) => BindMaterialDecalCells,
            ("MaterialDecalReferences", ResourceBindingType.BufferSrv) => BindMaterialDecalReferences,
            _ => null!,
        };
        return binder is not null;
    }

    public bool Prepare()
    {
        _decalSources.Clear();
        List<MaterialDecalSource> collected = _decalSources;
        _decalWorld.ExecuteQuery(_decalQuery, ref collected,
            static (QueryCursor rows, ref List<MaterialDecalSource> values) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    RenderMaterialDecal decal = row.Read<RenderMaterialDecal>();
                    values.Add(new(row.Entity, row.Read<RenderTransform>(), decal.Material, decal.Material.Revision, decal.Order));
                }
            });
        MaterialDecalSource[] sources = _decalSources.OrderBy(value => value.Order)
            .ThenBy(value => value.Entity.Index).ToArray();
        if (sources.AsSpan().SequenceEqual(_data.Sources)) return false;
        var volumes = new MaterialDecalVolume[Math.Max(1, sources.Length)];
        var properties = new List<byte>();
        var offsets = new Dictionary<Material, uint>();
        var reads = new List<RenderMaterialTextureRead>();
        for (int index = 0; index < sources.Length; index++)
        {
            MaterialDecalSource source = sources[index];
            if (!offsets.TryGetValue(source.Material, out uint address))
            {
                Shader shader = source.Material.Shader!;
                var key = (shader, source.Material.GetType());
                if (!_decalLayouts.TryGetValue(key, out ScalarLayout.ScalarMaterialLayout? layout))
                {
                    TypeLayoutReflection type = ResolveMaterialTypeLayout(shader,
                        shader.RequireSlangTarget(_device.Capabilities.CompileTarget), "MaterialDecalScalars");
                    layout = ScalarLayout.FromSlangByteAddress(type.Type.Name, type).Resolve(source.Material);
                    _decalLayouts.Add(key, layout);
                }
                byte[] bytes = layout.Encode(source.Material, _renderDevice.GetTextureDescriptorIndex,
                    _renderDevice.GetSamplerDescriptorIndex);
                address = (uint)properties.Count;
                offsets.Add(source.Material, address);
                properties.AddRange(bytes);
                reads.AddRange(layout.TextureProperties.Select(property => new RenderMaterialTextureRead(
                    source.Material, property, source.Material.GetTexture(property))));
            }
            RenderTransform transform = source.Transform;
            Matrix4x4 decalToWorld = new TransformQvvs(transform.Position, transform.Rotation, transform.Scale)
                { Stretch = transform.Stretch }.ToMatrix();
            Matrix4x4.Invert(decalToWorld, out Matrix4x4 worldToDecal);
            volumes[index] = new() { DecalToWorld = decalToWorld, WorldToDecal = worldToDecal, Parameters = address };
        }
        ImmutableArray<RenderMaterialTextureRead> textureReads = reads.OrderBy(value => value.Material.MaterialId)
            .ThenBy(value => value.Property).ToImmutableArray();
        bool resourcesChanged = !_data.TextureReads.AsSpan().SequenceEqual(textureReads.AsSpan());
        byte[] volumeBytes = MemoryMarshal.AsBytes(volumes.AsSpan()).ToArray();
        byte[] propertyBytes = properties.Count == 0 ? new byte[16] : properties.ToArray();
        _binning.SetCapacity((uint)sources.Length);
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            ResizeBuffer(ref edit, _data.Volumes, volumeBytes.Length);
            ResizeBuffer(ref edit, _data.Properties, propertyBytes.Length);
            edit.Commit();
            SetBufferBytes(_data.Volumes, volumeBytes);
            SetBufferBytes(_data.Properties, propertyBytes);
            _data.Sources = sources;
            _data.TextureReads = textureReads;
            _data.Version++;
        }
        finally { edit.Dispose(); }
        return resourcesChanged;
    }

    public bool Stage(ref RenderGraphFrame graph, scoped ReadOnlySpan<RenderView> views)
    {
        UploadData(ref graph);
        DataBuffer volumes = _buffers[_data.Volumes];
        _binning.Record(ref graph, new GraphBufferSlice(_data.Volumes, new(0, (ulong)volumes.Bytes.Length)),
            (uint)_data.Sources.Length, views);
        Span<uint> parameters = stackalloc uint[4]
        {
            (uint)_data.Sources.Length, _binning.CellCountPerView * _viewCount,
            _binning.ListCapacity, _settings.Index == MaterialDecalIndex.FullScan ? 0u : 1u,
        };
        MemoryMarshal.AsBytes(parameters).CopyTo(graph.GetBufferDataWriteSpan(_uniforms));
        bool reuse = _data.Completed && _data.Version == _data.CompletedVersion &&
            _data.CompletedListCapacity == _binning.ListCapacity && views.SequenceEqual(_data.Views);
        graph.SetRegionEnabled(_binning.Region, !reuse);
        if (!reuse) graph.ObserveExecution(new DecalSubmission(_data, _data.Version, _binning.ListCapacity, views.ToArray()));
        return reuse;
    }

    private readonly record struct MaterialDecalSource(Entity Entity, RenderTransform Transform,
        Material Material, ulong Revision, int Order);

    private sealed class MaterialDecalData(GraphBufferId volumes, GraphBufferId properties)
    {
        internal readonly GraphBufferId Volumes = volumes, Properties = properties;
        internal MaterialDecalSource[] Sources = [];
        internal ImmutableArray<RenderMaterialTextureRead> TextureReads = [];
        internal RenderView[] Views = [];
        internal uint Version = 1, CompletedVersion, CompletedListCapacity;
        internal bool Completed;
    }

    private sealed class DecalSubmission(MaterialDecalData data, uint version, uint listCapacity, RenderView[] views)
        : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            data.Completed = true;
            data.CompletedVersion = version;
            data.CompletedListCapacity = listCapacity;
            data.Views = views;
        }
    }

    private void BindMaterialDecalParameters(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferCbv(_uniforms), sync);
    private void BindMaterialDecalVolumes(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.Volumes, structureStride: MaterialDecalVolume.SizeInBytes), sync,
            BufferContentScope.CountBoundedPrefix);
    private void BindMaterialDecalProperties(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        pass.Bind(graph.CreateBufferSrv(_data.Properties), sync);
        _materials.ReadTextures(ref graph, ref pass, _data.TextureReads, sync);
    }
    private void BindMaterialDecalViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.ViewParameters, structureStride: MaterialDecalBinningView.SizeInBytes), sync);
    private void BindMaterialDecalCells(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.Cells, structureStride: 8), sync);
    private void BindMaterialDecalReferences(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.Indices, structureStride: 4), sync, BufferContentScope.CountBoundedPrefix);

    private static TypeLayoutReflection ResolveMaterialTypeLayout(Shader shader, int targetIndex, string attributeName)
    {
        var materialTypeNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DeclReflection declaration in shader.GetSlangDeclarations())
            Visit(declaration);
        if (materialTypeNames.Count == 0)
            throw new InvalidDataException(
                $"Shader '{shader.Name}' exposes no {attributeName} type after IR restoration.");
        if (materialTypeNames.Count != 1)
        {
            throw new InvalidDataException(
                $"Shader '{shader.Name}' exposes multiple {attributeName} types.");
        }

        string typeName = materialTypeNames[0];
        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        TypeReflection type = reflection.FindTypeByName(typeName)
            ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has no linked {attributeName} type '{typeName}'.");
        // Surface storage uses the target buffer layout. Material scalar encoding
        // derives its raw-buffer contract separately from the reflected field types.
        TypeLayoutReflection layout = reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer)
            ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has no layout for {attributeName} type '{type.Name}'.");
        return layout;

        void Visit(DeclReflection declaration)
        {
            if (declaration == DeclReflection.Null)
                return;
            if (declaration.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection candidate = declaration.Type;
                if (candidate != TypeReflection.Null && HasAttribute(candidate, attributeName) &&
                    !string.IsNullOrWhiteSpace(candidate.Name) &&
                    seen.Add(candidate.Name))
                {
                    materialTypeNames.Add(candidate.Name);
                }
            }
            for (int index = 0; index < declaration.ChildrenCount; index++)
                Visit(declaration.GetChild(checked((uint)index)));
        }

        static bool HasAttribute(TypeReflection type, string name)
        {
            for (uint index = 0; index < type.AttributeCount; index++)
            {
                AttributeReflection attribute = type.GetAttribute(index);
                if (attribute != AttributeReflection.Null &&
                    string.Equals(attribute.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static (GraphBufferId Buffer, DataBuffer Data) CreateBufferData(ref RenderGraphEdit edit,
        byte[] bytes, string label)
    {
        GraphBufferId buffer = edit.CreatePersistentBuffer(new((ulong)bytes.Length,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination, label));
        GraphPassId upload = edit.AddBufferUploadPass(buffer);
        return (buffer, new DataBuffer(bytes, upload));
    }

    private void ResizeBuffer(ref RenderGraphEdit edit, GraphBufferId buffer, int length)
    {
        if (_buffers[buffer].Bytes.Length != length)
            edit.SetBufferDescription(buffer, _graph.GetBufferDescription(buffer) with { Size = (ulong)length });
    }

    private void SetBufferBytes(GraphBufferId buffer, byte[] bytes)
    {
        DataBuffer data = _buffers[buffer];
        if (data.Bytes.AsSpan().SequenceEqual(bytes)) return;
        data.Bytes = bytes;
        data.Version++;
    }

    private void UploadData(ref RenderGraphFrame frame)
    {
        foreach (var pair in _buffers)
            if (pair.Value.Version != pair.Value.SubmittedVersion)
            {
                frame.WriteBuffer(pair.Key, 0, pair.Value.Bytes);
                frame.ObserveExecution(new DataSubmission(pair.Value, pair.Value.Version));
            }
    }

    private sealed class DataBuffer(byte[] bytes, GraphPassId upload)
    {
        internal byte[] Bytes = bytes;
        internal GraphPassId Upload { get; } = upload;
        internal ulong Version = 1, SubmittedVersion;
    }

    private sealed class DataSubmission(DataBuffer buffer, ulong version) : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission) => buffer.SubmittedVersion = version;
    }

    public void Dispose()
    {
        _decalWorld.ReleaseQuery(_decalQuery);
        _binning.Dispose();
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                edit.Remove(_region);
                edit.Remove(_uniforms);
                foreach (var pair in _buffers) { edit.Remove(pair.Value.Upload); edit.Remove(pair.Key); }
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _buffers.Clear();
        _decalSources.Clear();
        _decalLayouts.Clear();
    }
}

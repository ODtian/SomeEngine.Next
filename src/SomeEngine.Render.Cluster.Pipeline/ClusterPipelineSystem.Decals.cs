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

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialDecalSource(Entity Entity, RenderTransform Transform, Material Material, ulong Revision, int Order);

    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct MaterialDecalVolume
    {
        internal Matrix4x4 WorldToDecal, DecalToWorld;
        internal uint Parameters, Padding0, Padding1, Padding2;
    }

    private sealed class MaterialDecalData
    {
        internal GraphRegionId Geometry;
        internal GraphBufferId Volumes, Properties, Counts, Offsets, Writes, References, Ranges, Work, EmptyViews;
        internal GraphBufferSlice Parameters;
        internal GraphPassId[] Builds = [];
        internal MaterialDecalSource[] Sources = [];
        internal ImmutableArray<IndexedTextureRead> TextureReads = [];
        internal RenderView[] Views = [];
        internal uint Version, CompletedVersion;
        internal bool Completed, Reused;
    }

    private sealed class MaterialDecalResources(ClusterPipelineSystem pipeline, IGraphResourceProvider? features) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            binder = name switch
            {
                "MaterialDecalUniforms" => pipeline.BindMaterialDecalParameters,
                "MaterialDecalVolumes" => pipeline.BindMaterialDecalVolumes,
                "MaterialDecalProperties" => pipeline.BindMaterialDecalProperties,
                "MaterialDecalLightingViews" => pipeline.BindMaterialDecalViews,
                "MaterialDecalCounts" => pipeline.BindMaterialDecalCounts,
                "MaterialDecalOffsets" => pipeline.BindMaterialDecalOffsets,
                "MaterialDecalReferences" => pipeline.BindMaterialDecalReferences,
                _ => null!,
            };
            if (binder is not null) return true;
            return features?.TryResolve(name, type, out binder) == true;
        }
    }

    private QueryHandle _decalQuery;
    private RenderWorld? _decalWorld;
    private readonly List<MaterialDecalSource> _decalSources = [];
    private readonly Dictionary<(Shader Shader, Type Type), ScalarLayout.ScalarMaterialLayout> _decalLayouts = [];

    private void InstallMaterialDecals(ref RenderGraphEdit edit)
    {
        uint cells = _viewGrid.CellCountPerView * _outputDescription.ViewCount;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        var data = new MaterialDecalData
        {
            Geometry = edit.AddRegion("Cluster material decal cells", _regions.PrimaryGeometry),
            Volumes = CreateBufferData(ref edit, new byte[144], BufferUsages.ShaderRead, "Material decal volumes"),
            Properties = CreateBufferData(ref edit, new byte[16], BufferUsages.ShaderRead, "Material decal properties"),
            Counts = edit.CreatePersistentBuffer(new(Math.Max(4ul, cells * 4ul), storage, "Material decal cell counts")),
            Offsets = edit.CreatePersistentBuffer(new(Math.Max(4ul, cells * 4ul), storage, "Material decal cell offsets")),
            Writes = edit.CreatePersistentBuffer(new(Math.Max(4ul, cells * 4ul), storage, "Material decal cell writes")),
            References = edit.CreatePersistentBuffer(new(_options.BlendReferenceCapacity * 4ul, storage, "Material decal references")),
            Ranges = edit.CreatePersistentBuffer(new(Math.Max(32ul, _outputDescription.ViewCount * 32ul), storage, "Material decal cell ranges")),
            Work = edit.CreatePersistentBuffer(new(16, storage | BufferUsages.CopySource, "Material decal work")),
            Parameters = CreateParameters(ref edit, new byte[16], "Material decal parameters"),
        };
        if (_viewGrid.Parameters is null)
            data.EmptyViews = CreateBufferData(ref edit, new byte[192], BufferUsages.ShaderRead, "Material decal empty views");
        GraphData.Decals = data;
        Span<uint> parameters = stackalloc uint[4] { 0, cells, _options.BlendReferenceCapacity, _options.BlendIndex == ClusterBlendIndex.FullScan ? 0u : 1u };
        UpdateParameters(ref edit, data.Parameters, MemoryMarshal.AsBytes(parameters));
        ClusterComputePipeline[] pipelines = [_pipelines!.DecalClear, _pipelines.DecalCount, _pipelines.DecalReserve, _pipelines.DecalScatter, _pipelines.DecalSort];
        string[] entries = ["ClearMaterialDecalCells", "CountMaterialDecalCells", "ReserveMaterialDecalCells", "ScatterMaterialDecalCells", "SortMaterialDecalCells"];
        Shader shader = _configuration.Operations.Single(value => value.Role == ClusterShaderOperationRole.MaterialDecalCells).Shaders[0];
        int target = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        data.Builds = new GraphPassId[pipelines.Length];
        for (int index = 0; index < pipelines.Length; index++)
        {
            ClusterComputePipeline pipeline = pipelines[index];
            uint work = index is 1 or 3 ? 1 : cells;
            GraphPassId passId = AddComputePass(ref edit, entries[index], pipeline,
                new ClusterDispatchParameters(new(Math.Max(1u, (work + 63u) / 64u), 1, 1)), 0,
                ClusterDispatchParameters.Record, region: data.Geometry);
            data.Builds[index] = passId;
            if (index != 0) edit.OrderAfter(passId, data.Builds[index - 1]);
            PassDefinition pass = edit.GetPass(passId);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            var layout = ClusterLinkedStageLayout.Create(pipeline.ParameterLayout,
                (category, space, register) => shader.IsSlangParameterLocationUsed(target, entries[index], category, space, register));
            foreach (var resource in layout.Resources)
            {
                if (BindInactiveStageResource(ref pass, resource)) continue;
                if (resource.Name == "MaterialDecalUniforms") { BindMaterialDecalParameters(ref edit, ref pass, PipelineSync.ComputeShading); continue; }
                if (resource.Name == "MaterialDecalLightingViews") { BindMaterialDecalViews(ref edit, ref pass, PipelineSync.ComputeShading); continue; }
                GraphBufferId buffer = resource.Name switch
                {
                    "MaterialDecalVolumes" => data.Volumes,
                    "MaterialDecalCountOutput" => data.Counts,
                    "MaterialDecalOffsetOutput" => data.Offsets,
                    "MaterialDecalWrites" => data.Writes,
                    "MaterialDecalReferenceOutput" => data.References,
                    "MaterialDecalRanges" => data.Ranges,
                    "MaterialDecalWork" => data.Work,
                    _ => throw new InvalidDataException($"Unknown material decal resource '{resource.Name}'."),
                };
                uint stride = resource.Name switch { "MaterialDecalVolumes" => 144u, "MaterialDecalRanges" => 32u, "MaterialDecalWork" => 0u, _ => 4u };
                BufferContentScope scope = resource.Name is "MaterialDecalVolumes" or "MaterialDecalRanges" or "MaterialDecalReferenceOutput"
                    ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange;
                if (resource.Type == ResourceBindingType.BufferSrv)
                    pass.Bind(edit.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading, scope);
                else
                {
                    GraphAccessMode mode = (index, resource.Name) switch
                    {
                        (0, _) => GraphAccessMode.Write,
                        (1, "MaterialDecalRanges") => GraphAccessMode.Write,
                        (1, _) => GraphAccessMode.ReadWrite,
                        (2, "MaterialDecalOffsetOutput") => GraphAccessMode.Write,
                        (2, "MaterialDecalWork") => GraphAccessMode.ReadWrite,
                        (3, "MaterialDecalReferenceOutput") => GraphAccessMode.Write,
                        (3, "MaterialDecalWrites") => GraphAccessMode.ReadWrite,
                        (4, "MaterialDecalReferenceOutput") => GraphAccessMode.ReadWrite,
                        _ => GraphAccessMode.Read,
                    };
                    pass.Bind(edit.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading, mode,
                        index is 0 or 2 ? WriteCoverage.Complete : WriteCoverage.Partial,
                        ResourceContentState.Defined, scope);
                }
            }
        }
    }

    private bool UpdateMaterialDecals()
    {
        MaterialDecalData data = GraphData.Decals!;
        _decalSources.Clear();
        List<MaterialDecalSource> collected = _decalSources;
        _decalWorld!.ExecuteQuery(_decalQuery, ref collected, static (QueryCursor rows, ref List<MaterialDecalSource> values) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                RenderMaterialDecal decal = row.Read<RenderMaterialDecal>();
                values.Add(new(row.Entity, row.Read<RenderTransform>(), decal.Material, decal.Material.Revision, decal.Order));
            }
        });
        MaterialDecalSource[] sources = _decalSources.OrderBy(value => value.Order).ThenBy(value => value.Entity.Index).ToArray();
        if (sources.AsSpan().SequenceEqual(data.Sources)) return false;
        var volumes = new MaterialDecalVolume[Math.Max(1, sources.Length)];
        var properties = new List<byte>();
        var offsets = new Dictionary<Material, uint>();
        var reads = new List<IndexedTextureRead>();
        for (int index = 0; index < sources.Length; index++)
        {
            MaterialDecalSource source = sources[index];
            if (!offsets.TryGetValue(source.Material, out uint address))
            {
                Shader shader = source.Material.Shader!;
                var key = (shader, source.Material.GetType());
                if (!_decalLayouts.TryGetValue(key, out var layout))
                {
                    var type = ClusterShadeStages.ResolveMaterialTypeLayout(shader,
                        shader.RequireSlangTarget(_device.Capabilities.CompileTarget), "MaterialDecalScalars");
                    layout = ScalarLayout.FromSlangByteAddress(type.Type.Name, type).Resolve(source.Material);
                    _decalLayouts.Add(key, layout);
                }
                byte[] bytes = layout.Encode(source.Material, _renderDevice.GetTextureDescriptorIndex, _renderDevice.GetSamplerDescriptorIndex);
                address = (uint)properties.Count;
                offsets.Add(source.Material, address);
                properties.AddRange(bytes);
                reads.AddRange(layout.TextureProperties.Select(property => new IndexedTextureRead(source.Material, property, source.Material.GetTexture(property))));
            }
            RenderTransform transform = source.Transform;
            Matrix4x4 decalToWorld = new TransformQvvs(transform.Position, transform.Rotation, transform.Scale) { Stretch = transform.Stretch }.ToMatrix();
            Matrix4x4.Invert(decalToWorld, out Matrix4x4 worldToDecal);
            volumes[index] = new() { DecalToWorld = decalToWorld, WorldToDecal = worldToDecal, Parameters = address };
        }
        ImmutableArray<IndexedTextureRead> textureReads = reads.OrderBy(value => value.Material.MaterialId).ThenBy(value => value.Property).ToImmutableArray();
        bool resourcesChanged = !data.TextureReads.AsSpan().SequenceEqual(textureReads.AsSpan());
        byte[] volumeBytes = MemoryMarshal.AsBytes(volumes.AsSpan()).ToArray(), propertyBytes = properties.Count == 0 ? new byte[16] : properties.ToArray();
        EditGraph((ref RenderGraphEdit edit) =>
        {
            UpdateBufferData(ref edit, data.Volumes, ClusterBytes.FromBytes(volumeBytes), [new(0, (ulong)volumeBytes.Length)]);
            UpdateBufferData(ref edit, data.Properties, ClusterBytes.FromBytes(propertyBytes), [new(0, (ulong)propertyBytes.Length)]);
            edit.SetBufferDescription(data.Ranges, new(Math.Max(32ul, (ulong)sources.Length * _outputDescription.ViewCount * 32),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Material decal cell ranges"));
            Span<uint> parameters = stackalloc uint[4] { (uint)sources.Length, _viewGrid.CellCountPerView * _outputDescription.ViewCount,
                _options.BlendReferenceCapacity, _options.BlendIndex == ClusterBlendIndex.FullScan ? 0u : 1u };
            UpdateParameters(ref edit, data.Parameters, MemoryMarshal.AsBytes(parameters));
        });
        data.Sources = sources; data.TextureReads = textureReads; data.Version++;
        return resourcesChanged;
    }

    private void BindMaterialDecalFrame(ref RenderGraphFrame graph, scoped ReadOnlySpan<RenderView> views)
    {
        MaterialDecalData data = GraphData.Decals!;
        bool reuse = data.Completed && data.Version == data.CompletedVersion && views.SequenceEqual(data.Views);
        data.Reused = reuse;
        foreach (GraphPassId pass in data.Builds) graph.SetPassEnabled(pass, !reuse);
        if (reuse) return;
        data.Completed = false;
        data.Views = views.ToArray(); data.CompletedVersion = data.Version;
        uint groups = Math.Max(1u, ((uint)data.Sources.Length * _outputDescription.ViewCount + 63u) / 64u);
        var dispatch = new ClusterDispatchParameters(new(groups, 1, 1));
        graph.SetPassDataIfChanged(data.Builds[1], dispatch); graph.SetPassDataIfChanged(data.Builds[3], dispatch);
    }

    private void CompleteMaterialDecalFrame(RenderGraphFrameResult result)
    {
        if (GraphData.Decals is { } data && result.TryGetPassCompletion(data.Builds[^1], out _)) data.Completed = true;
    }

    private void BindMaterialDecalParameters(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(GraphData.Decals!.Parameters, sync);
    private void BindMaterialDecalVolumes(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.Volumes, structureStride: 144), sync, BufferContentScope.CountBoundedPrefix);
    private void BindMaterialDecalProperties(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.Properties), sync);
    private void BindMaterialDecalViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        if (_viewGrid.Parameters is { } views) pass.Bind(graph.CreateBufferSrv(views, structureStride: 192), sync);
        else pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.EmptyViews, structureStride: 192), sync);
    }
    private void BindMaterialDecalCounts(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.Counts, structureStride: 4), sync);
    private void BindMaterialDecalOffsets(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.Offsets, structureStride: 4), sync);
    private void BindMaterialDecalReferences(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(GraphData.Decals!.References, structureStride: 4), sync, BufferContentScope.CountBoundedPrefix);
}

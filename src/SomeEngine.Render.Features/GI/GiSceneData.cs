using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Features.GI;

/// <summary>Uploads shared mesh distance fields indexed by the existing Cluster roots.</summary>
public sealed class GiSceneData : IRenderGraphFrameExecutionObserver, IDisposable
{
    private const BufferUsages FieldUsages = BufferUsages.ShaderRead | BufferUsages.CopyDestination;
    private readonly RenderWorld _world;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly Dictionary<uint, MeshField> _fields = [];
    private readonly List<uint> _removedRoots = [];
    private readonly GraphPassId _meshUpload;
    private QueryHandle _query;
    private int _meshByteLength = 4;
    private MeshFieldIndex[] _index = [new(uint.MaxValue, 0)];
    private ulong _collection;
    private bool _fieldsChanged;
    private bool _uploadRequired = true;

    public GiSceneData(RenderWorld world, global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        _world = world;
        _graph = graph;
        _query = world.Query(new QueryDefinitionBuilder().Read<RenderMesh>().Read<RenderClusterGeometry>()
            .ReadBuffer<RenderMaterialBinding>());
        using RenderGraphEdit edit = graph.BeginEdit();
        MeshFields = edit.CreatePersistentBuffer(new BufferDesc(4, FieldUsages, "GI mesh distance fields"));
        MeshFieldsIndex = edit.CreatePersistentBuffer(new BufferDesc(8, FieldUsages, "GI mesh field index"));
        _meshUpload = edit.AddBufferUploadPass(MeshFields);
        UploadPass = edit.AddBufferUploadPass(MeshFieldsIndex);
        edit.OrderAfter(UploadPass, _meshUpload);
        edit.Commit();
    }

    public GraphBufferId MeshFields { get; }
    /// <summary>One uint2 per root: distance-field byte address and material-binding range length.</summary>
    public GraphBufferId MeshFieldsIndex { get; }
    public GraphPassId UploadPass { get; }
    public uint MeshFieldsIndexCount => (uint)_index.Length;

    /// <summary>Runs after geometry preparation and before BeginFrame; returns whether field data changed.</summary>
    public bool Reconcile()
    {
        _collection++;
        GiSceneData data = this;
        _world.ExecuteQuery(_query, ref data, static (QueryCursor rows, ref GiSceneData data) =>
        {
            foreach (QueryRow row in rows.Rows)
                data.ReadMesh(row.Read<RenderMesh>(), row.Read<RenderClusterGeometry>().Geometry,
                    (uint)row.ReadBuffer<RenderMaterialBinding>().Count);
        });
        _removedRoots.Clear();
        foreach ((uint root, MeshField field) in _fields)
            if (field.Collection != _collection) _removedRoots.Add(root);
        foreach (uint root in _removedRoots)
        {
            _fields.Remove(root);
            _fieldsChanged = true;
        }
        if (!_fieldsChanged) return false;
        UpdateFieldIndex();
        _fieldsChanged = false;
        _uploadRequired = true;
        return true;
    }

    private void ReadMesh(RenderMesh source, ClusterGeometry geometry, uint materialCount)
    {
        if (source.DistanceField is not { } field) return;
        if (_fields.TryGetValue(geometry.RootBvh, out MeshField? current) &&
            ReferenceEquals(current.DistanceField, field) && current.Allocation == geometry.Allocation &&
            current.Mesh.Bvh.Equals(source.Mesh.Bvh) && current.Mesh.PageDigests == source.Mesh.PageDigests)
        {
            if (current.Collection != _collection && current.MaterialCount != materialCount)
            {
                current.MaterialCount = materialCount;
                _fieldsChanged = true;
            }
            current.Collection = _collection;
            return;
        }
        _fields[geometry.RootBvh] = new(source.Mesh, field, geometry.Allocation, materialCount, _collection);
        _fieldsChanged = true;
    }

    private void UpdateFieldIndex()
    {
        KeyValuePair<uint, MeshField>[] fields = _fields.OrderBy(value => value.Key).ToArray();
        int byteCount = 0;
        foreach (KeyValuePair<uint, MeshField> field in fields)
            byteCount = AlignField(byteCount) + field.Value.DistanceField.GpuByteLength;
        int byteLength = Math.Max(4, AlignField(byteCount));
        MeshFieldIndex[] index = new MeshFieldIndex[fields.Length == 0 ? 1 : checked((int)fields[^1].Key + 1)];
        Array.Fill(index, new MeshFieldIndex(uint.MaxValue, 0));
        int offset = 0;
        foreach ((uint root, MeshField field) in fields)
        {
            offset = AlignField(offset);
            index[root] = new((uint)offset, field.MaterialCount);
            offset += field.DistanceField.GpuByteLength;
        }
        if (byteLength != _meshByteLength || index.Length != _index.Length)
        {
            using RenderGraphEdit edit = _graph.BeginEdit();
            edit.SetBufferDescription(MeshFields,
                new BufferDesc((ulong)byteLength, FieldUsages, "GI mesh distance fields"));
            edit.SetBufferDescription(MeshFieldsIndex,
                new BufferDesc((ulong)index.Length * 8u, FieldUsages, "GI mesh field index"));
            edit.Commit();
        }
        _meshByteLength = byteLength;
        _index = index;
    }

    private static int AlignField(int bytes) => (bytes + 15) & ~15;

    public void Record(ref RenderGraphFrame graph)
    {
        if (!_uploadRequired) return;
        Span<byte> bytes = graph.GetWriteSpan(MeshFields, 0, _meshByteLength);
        if (_fields.Count == 0) bytes.Clear();
        foreach ((uint root, MeshField field) in _fields)
        {
            int offset = (int)_index[root].DistanceFieldAddress;
            int length = field.DistanceField.GpuByteLength;
            field.DistanceField.WriteGpuData(bytes.Slice(offset, length),
                field.Mesh, field.Allocation.FirstBvhNode);
            bytes.Slice(offset + length, AlignField(length) - length).Clear();
        }
        graph.WriteBuffer(MeshFieldsIndex, 0, MemoryMarshal.AsBytes(_index.AsSpan()));
        graph.ObserveExecution(this);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_meshUpload, out _) && result.TryGetPassCompletion(UploadPass, out _))
            _uploadRequired = false;
    }

    public void Dispose()
    {
        if (_query.IsValid) _world.ReleaseQuery(_query);
        _query = default;
        _fields.Clear();
        _removedRoots.Clear();
        _index = [];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct MeshFieldIndex(uint DistanceFieldAddress, uint MaterialCount);

    private sealed class MeshField(Mesh mesh, MeshDistanceField field,
        ClusterGeometryAllocation allocation, uint materialCount, ulong collection)
    {
        internal readonly Mesh Mesh = mesh;
        internal readonly MeshDistanceField DistanceField = field;
        internal readonly ClusterGeometryAllocation Allocation = allocation;
        internal uint MaterialCount = materialCount;
        internal ulong Collection = collection;
    }
}

using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using System.Numerics;

namespace SomeEngine.Render.Features.Shadows;

internal sealed class ShadowCasterCache(VirtualShadowFeature feature)
{
    private readonly VirtualShadowFeature _feature = feature;

    internal GraphBufferInput _frameDepthCacheClasses = null!;
    internal GraphBufferInput _framePreviousDepthCacheClasses = null!;
    internal GraphBufferInput _frameLodBounds = null!;
    internal GraphBufferInput _framePreviousCasterRows = null!;
    internal byte[] _casterCacheAges = [];
    internal Entity[] _casterEntities = [];
    internal uint _casterInputVersion;
    internal uint[] _casterStaticBits = [];
    private uint[] _previousCasterRowsData = [];

    internal void UpdateCasterCacheClasses(ref RenderGraphFrame graph, int instanceCount)
    {
        VirtualShadowFrame data = _feature.GetFrame(ref graph);
        data._lodCasterBounds.Clear();
        ReadOnlySpan<DepthCacheClass> classes = _feature._geometry.CollectInstanceCacheInputs(
            data._lodCasterBounds, out data._frameCasterInputVersion);
        bool contentChanged = data._frameInstanceRevision != _feature._historyInstanceRevision ||
            _casterEntities.Length != instanceCount;
        data._rebuiltCasterChanges.Clear();
        if (contentChanged)
        {
            data._frameCasterEntities = new Entity[instanceCount];
            _feature._geometry.CollectInstanceEntities(data._frameCasterEntities, _casterInputVersion,
                data._rebuiltCasterChanges);
        }
        else data._frameCasterEntities = _casterEntities;
        data._rebuiltCasterChanges.AddRange(data._rasterChangedInstances);
        data._rebuiltCasterChanges.Sort();
        bool rowsChanged = !data._frameCasterEntities.AsSpan().SequenceEqual(_casterEntities);
        Dictionary<Entity, Queue<(uint Row, byte Age)>>? previous = null;
        if (rowsChanged)
        {
            previous = new(_casterEntities.Length);
            for (int row = 0; row < _casterEntities.Length; row++)
            {
                Entity entity = _casterEntities[row];
                if (!previous.TryGetValue(entity, out var rows)) previous.Add(entity, rows = new());
                rows.Enqueue(((uint)row, _casterCacheAges[row]));
            }
            data._changedInstances.Clear();
        }
        data._removedCasterRows.Clear();
        int previousRowCount = Math.Max(instanceCount, 1);
        if (_previousCasterRowsData.Length < previousRowCount) Array.Resize(ref _previousCasterRowsData, previousRowCount);
        Span<uint> previousRows = _previousCasterRowsData.AsSpan(0, previousRowCount);
        previousRows.Fill(uint.MaxValue);
        if (data._frameCasterCacheAges.Length != instanceCount) data._frameCasterCacheAges = new byte[instanceCount];
        int wordCount = Math.Max((instanceCount + 31) / 32, 1);
        if (data._frameCasterStaticBits.Length != wordCount) data._frameCasterStaticBits = new uint[wordCount];
        else Array.Clear(data._frameCasterStaticBits);
        int changedIndex = 0;
        int changedCount = data._changedInstances.Count;
        data._returnedStaticCasters.Clear();
        for (int row = 0; row < instanceCount; row++)
        {
            bool changed = !rowsChanged && changedIndex < changedCount && data._changedInstances[changedIndex] == row;
            if (changed) changedIndex++;
            byte previousAge = 101;
            uint previousRow = (uint)row;
            bool added = false;
            if (rowsChanged)
            {
                Entity entity = data._frameCasterEntities[row];
                added = !previous!.TryGetValue(entity, out var oldRows);
                if (added) previousRow = uint.MaxValue;
                else
                {
                    var old = oldRows!.Dequeue();
                    previousRow = old.Row;
                    previousAge = old.Age;
                    if (oldRows.Count == 0) previous.Remove(entity);
                }
                changed = added || data._rebuiltCasterChanges.BinarySearch((uint)row) >= 0;
                if (changed) data._changedInstances.Add((uint)row);
            }
            else previousAge = _casterCacheAges[row];
            previousRows[row] = previousRow;
            if (_feature._invalidateAllCasters && !changed)
                data._returnedStaticCasters.Add((uint)row);
            byte age = added ? (byte)101 : changed || _feature._invalidateAllCasters ? (byte)0 :
                (byte)Math.Min(previousAge + 1, 101);
            data._frameCasterCacheAges[row] = age;
            bool isStatic = classes[row] == DepthCacheClass.Static || classes[row] == DepthCacheClass.Auto && age > 100;
            if (isStatic) data._frameCasterStaticBits[row / 32] |= 1u << (row & 31);
            bool wasStatic = previousRow != uint.MaxValue &&
                (_casterStaticBits[previousRow / 32] & (1u << (int)(previousRow & 31))) != 0u;
            if (isStatic && !wasStatic && !changed && !_feature._invalidateAllCasters)
                data._returnedStaticCasters.Add((uint)row);
        }
        if (previous is not null)
            foreach (var rows in previous.Values)
                foreach (var old in rows) data._removedCasterRows.Add(old.Row);
        data._changedInstances.AddRange(data._returnedStaticCasters);
        if (data._returnedStaticCasters.Count != 0) data._changedInstances.Sort();
        graph.SetBufferInput(_frameDepthCacheClasses, graph.Upload<uint>(data._frameCasterStaticBits,
            BufferUsages.ShaderRead, "Virtual shadow effective static caster bits"));
        uint empty = 0;
        graph.SetBufferInput(_framePreviousDepthCacheClasses, graph.Upload<uint>(
            _casterStaticBits.Length != 0 ? _casterStaticBits : System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref empty, 1),
            BufferUsages.ShaderRead, "Virtual shadow previous static caster bits"));
        graph.SetBufferInput(_framePreviousCasterRows, graph.Upload<uint>(previousRows,
            BufferUsages.ShaderRead, "Virtual shadow previous caster rows"));
        graph.SetBufferInput(_frameLodBounds, graph.Upload<Vector4>(
            data._lodCasterBounds.Count != 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data._lodCasterBounds) : [Vector4.Zero],
            BufferUsages.ShaderRead, "Virtual shadow changed LOD bounds"));
    }
    internal void CommitCasterCacheClasses(VirtualShadowFrame data)
    {
        _casterCacheAges = data._frameCasterCacheAges;
        _casterStaticBits = data._frameCasterStaticBits;
        _casterEntities = data._frameCasterEntities;
        _casterInputVersion = data._frameCasterInputVersion;
    }

    internal void BindRasterDepthCacheClasses(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_frameDepthCacheClasses, structureStride: sizeof(uint)), sync);

}

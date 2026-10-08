using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphBufferInput _frameDepthCacheClasses = null!;
    private GraphBufferInput _framePreviousDepthCacheClasses = null!;
    private GraphBufferInput _frameLodBounds = null!;
    private byte[] _casterCacheAges = [];
    private byte[] _frameCasterCacheAges = [];
    private uint[] _casterStaticBits = [];
    private uint[] _frameCasterStaticBits = [];
    private readonly List<Vector4> _lodCasterBounds = [];
    private readonly List<uint> _returnedStaticCasters = [];

    private void UpdateCasterCacheClasses(ref RenderGraphFrame graph, int instanceCount)
    {
        bool newInstances = !ReferenceEquals(_frameInstances, _committedInstances);
        _lodCasterBounds.Clear();
        ReadOnlySpan<DepthCacheClass> classes = _geometry.CollectCasterCacheInputs(_lodCasterBounds);
        if (_frameCasterCacheAges.Length != instanceCount) _frameCasterCacheAges = new byte[instanceCount];
        int wordCount = Math.Max((instanceCount + 31) / 32, 1);
        if (_frameCasterStaticBits.Length != wordCount) _frameCasterStaticBits = new uint[wordCount];
        else Array.Clear(_frameCasterStaticBits);
        int changedIndex = 0;
        int changedCount = _changedInstances.Count;
        _returnedStaticCasters.Clear();
        for (int row = 0; row < instanceCount; row++)
        {
            bool changed = changedIndex < changedCount && _changedInstances[changedIndex] == row;
            if (changed) changedIndex++;
            byte age = newInstances ? (byte)101 : changed || _invalidateAllCasters ? (byte)0 : (byte)Math.Min(_casterCacheAges[row] + 1, 101);
            _frameCasterCacheAges[row] = age;
            bool isStatic = classes[row] == DepthCacheClass.Static ||
                classes[row] == DepthCacheClass.Auto && age > 100;
            if (isStatic) _frameCasterStaticBits[row / 32] |= 1u << (row & 31);
            if (!newInstances && isStatic && (_casterStaticBits[row / 32] & (1u << (row & 31))) == 0u && !changed)
                _returnedStaticCasters.Add((uint)row);
        }
        _changedInstances.AddRange(_returnedStaticCasters);
        if (_returnedStaticCasters.Count != 0) _changedInstances.Sort();
        graph.SetBufferInput(_frameDepthCacheClasses, graph.Upload<uint>(_frameCasterStaticBits,
            BufferUsages.ShaderRead, "Virtual shadow effective static caster bits"));
        graph.SetBufferInput(_framePreviousDepthCacheClasses, graph.Upload<uint>(
            newInstances ? _frameCasterStaticBits : _casterStaticBits,
            BufferUsages.ShaderRead, "Virtual shadow previous static caster bits"));
        graph.SetBufferInput(_frameLodBounds, graph.Upload<Vector4>(
            _lodCasterBounds.Count != 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_lodCasterBounds) : [Vector4.Zero],
            BufferUsages.ShaderRead, "Virtual shadow changed LOD bounds"));
    }

    private void CommitCasterCacheClasses()
    {
        (_casterCacheAges, _frameCasterCacheAges) = (_frameCasterCacheAges, _casterCacheAges);
        (_casterStaticBits, _frameCasterStaticBits) = (_frameCasterStaticBits, _casterStaticBits);
    }

    private void BindRasterDepthCacheClasses(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_frameDepthCacheClasses, structureStride: sizeof(uint)), sync);
}

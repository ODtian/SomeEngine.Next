using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Resources.Layers;
using SomeEngine.Runtime;

namespace DefaultGame;

internal sealed partial class ClusterBlendScene
{
    private readonly List<(int Index, TransformQvvs Transform)> _orderedMasks = [];

    private void AddOrdered(SceneEdit edit)
    {
        _orderedMasks.Clear();
        var write = new StencilFaceState(StencilOperation.Keep, StencilOperation.Zero,
            StencilOperation.Replace, CompareOperation.Always);
        var read = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep,
            StencilOperation.Keep, CompareOperation.Equal);
        for (int window = 0; window < 4; window++)
        {
            byte reference = (byte)(window + 1);
            Vector3 center = new((window % 2 - .5f) * 1.05f, (window / 2 - .5f) * .8f, 2.8f);
            var mask = Unlit(Vector4.One, assets.Unlit.AlbedoMap!);
            SetLayerMaterial(mask, new LayerOperation
            {
                ColorWriteMask = ColorWriteMasks.None, DepthTestTarget = LayerDepthTarget.Opaque,
                DepthComparison = CompareOperation.LessOrEqual, StencilTest = true, Front = write, Back = write,
            }, (byte)(window * 4), reference);
            TransformQvvs maskTransform = new(center, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, window * .3f), .36f);
            _orderedMasks.Add((_objects.Count, maskTransform));
            Add(edit, assets.Panel, mask, maskTransform);
            var tint = Unlit(new(.25f + window * .15f, .6f, .9f - window * .15f, .55f), assets.Unlit.AlbedoMap!);
            LayerOperation operation = new()
            {
                StencilTest = true, StencilWriteMask = 0, Front = read, Back = read,
            };
            SetLayerMaterial(tint, operation, (byte)(window * 4 + 1), reference);
            Add(edit, assets.Panel, tint, new(center with { Z = 2.6f }, Quaternion.Identity, .5f));
            var multiply = Unlit(new(.55f, .9f, .65f, 1), assets.Unlit.AlbedoMap!);
            SetLayerMaterial(multiply, operation with { Color = LayerColorOperation.Multiply },
                (byte)(window * 4 + 2), reference);
            Add(edit, assets.Panel, multiply, new(center + new Vector3(.12f, 0, -.3f), Quaternion.Identity, .35f));
            var add = Unlit(new(.15f, .35f, .6f, .25f), assets.Unlit.AlbedoMap!);
            SetLayerMaterial(add, operation with
            {
                Color = LayerColorOperation.Add, ColorWriteMask = ColorWriteMasks.Green | ColorWriteMasks.Blue,
            }, (byte)(window * 4 + 3), reference);
            Add(edit, assets.Panel, add, new(center + new Vector3(-.12f, .08f, -.4f), Quaternion.Identity, .3f));
        }
    }

    private void AnimateOrdered(int frame)
    {
        if (effect != "ordered") return;
        foreach ((int index, TransformQvvs original) in _orderedMasks)
        {
            TransformQvvs transform = original;
            transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, index * .075f + MathF.Sin(frame / 60f) * .2f);
            _objects[index] = (_objects[index].Entity, transform);
        }
    }
}

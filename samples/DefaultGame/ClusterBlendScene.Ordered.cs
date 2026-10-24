using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Resources;
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
            mask.State = mask.State with
            {
                Ordered = true, DrawOrder = window * 4, StencilRef = reference,
                Blend = new(WriteMask: ColorWriteMasks.None),
                DepthStencil = new(DepthTest: true, DepthComparison: CompareOperation.LessOrEqual,
                    StencilTest: true, Front: write, Back: write),
            };
            TransformQvvs maskTransform = new(center, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, window * .3f), .36f);
            _orderedMasks.Add((_objects.Count, maskTransform));
            Add(edit, assets.Panel, mask, maskTransform);
            var tint = Unlit(new(.25f + window * .15f, .6f, .9f - window * .15f, .55f), assets.Unlit.AlbedoMap!);
            MaterialState state = tint.State with
            {
                Ordered = true, DrawOrder = window * 4 + 1, StencilRef = reference,
                Blend = new(Enabled: true, SourceColor: BlendFactor.SourceAlpha,
                    DestinationColor: BlendFactor.OneMinusSourceAlpha, SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One),
                DepthStencil = new(StencilTest: true, StencilWriteMask: 0, Front: read, Back: read),
            };
            tint.State = state;
            Add(edit, assets.Panel, tint, new(center with { Z = 2.6f }, Quaternion.Identity, .5f));
            var multiply = Unlit(new(.55f, .9f, .65f, 1), assets.Unlit.AlbedoMap!);
            multiply.State = state with
            {
                DrawOrder = window * 4 + 2,
                Blend = new(Enabled: true, SourceColor: BlendFactor.DestinationColor,
                    DestinationColor: BlendFactor.Zero, SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One),
            };
            Add(edit, assets.Panel, multiply, new(center + new Vector3(.12f, 0, -.3f), Quaternion.Identity, .35f));
            var add = Unlit(new(.15f, .35f, .6f, .25f), assets.Unlit.AlbedoMap!);
            add.State = state with
            {
                DrawOrder = window * 4 + 3,
                Blend = new(Enabled: true, SourceColor: BlendFactor.SourceAlpha, DestinationColor: BlendFactor.One,
                    SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One,
                    WriteMask: ColorWriteMasks.Green | ColorWriteMasks.Blue),
            };
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

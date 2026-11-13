using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources.Layers;

namespace SomeEngine.Render.Tests;

// #128: this contribution check executes the complete composition graph on hardware.
[Trait("Category", "Hardware")]
public sealed class LayerFragmentStoreTests
{
    [Fact]
    public void VisibleLayerContributionsRemainOnTheirTiles()
    {
        const float alpha = 1f / 512;
        LayerTestFragment[] fragments = Enumerable.Range(0, 770).Select(index =>
            new LayerTestFragment((uint)index % 2, 0, .1f + index * .001f,
                (uint)index + 128, Material: (uint)index % 2)).ToArray();
        LayerTestMaterial[] palette =
        [
            LayerTestMaterial.Over(Vector3.UnitX, alpha),
            LayerTestMaterial.Over(Vector3.UnitZ, alpha),
        ];
        using var scene = new LayerCompositionTestScene(width: 32);
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0), new(0)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);

        Assert.Equal(identity, scene.Composition.Metrics.FrameIdentity);
        Assert.Equal(770u, scene.Composition.Metrics.Fragments);
        Assert.Equal(0u, scene.Composition.Metrics.FragmentOverflow);
        float coverage = 1 - MathF.Pow(1 - alpha, 385);
        Assert.InRange(Vector3.Distance(Vector3.UnitX * coverage, scene.Colors[0]), 0, 1f / 255);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ * coverage, scene.Colors[16]), 0, 1f / 255);
    }
}

using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;
using SomeEngine.Resources.Layers;

namespace SomeEngine.Render.Tests;

// #128: WARP complete composition frames time out; these color checks use a discrete hardware adapter.
[Trait("Category", "Hardware")]
public sealed class LayerCompositionTests
{
    [Fact]
    public void ActiveGraphEditsPreserveThePassListWhenRegistrationFails()
    {
        using var scene = new LayerCompositionTestScene();
        LayerPassDesc[] descriptions = scene.Composition.Passes.ToArray();
        ulong version = scene.Graph.StructureVersion;
        using (RenderGraphEdit edit = scene.Graph.BeginEdit())
        {
            Assert.Throws<InvalidOperationException>(() => scene.Composition.AddPass(new("Another pass")));
            Assert.Equal(descriptions, scene.Composition.Passes.ToArray());
            Assert.Equal(version, scene.Graph.StructureVersion);
        }
    }

    [Fact]
    public void RegisteringAPassCommitsOneEditAndAddsItsColorContribution()
    {
        using var scene = new LayerCompositionTestScene();
        LayerTestFragment[] fragments = [new(0, 0, .2f, 128), new(0, 0, .4f, 256, Material: 1, Pass: 1)];
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(Vector3.UnitX, .5f), LayerTestMaterial.Over(Vector3.UnitZ, .5f)];
        ulong version = scene.Graph.StructureVersion;

        LayerPassId pass = scene.Composition.AddPass(new("Second color", ClipByOpaqueDepth: false));

        Assert.Equal(version + 1u, scene.Graph.StructureVersion);
        Assert.Equal(new[] { "Layer behavior", "Second color" }, scene.Composition.Passes.ToArray().Select(value => value.Name));
        scene.OrderColorCapture();
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
        Assert.InRange(Vector3.Distance(new(.25f, 0, .5f), scene.Colors[0]), 0, 1f / 255);
    }

    [Fact]
    public void CompletedOverflowFeedbackGrowsOnceBeforeTheReuseBoundaryFrame()
    {
        LayerTestFragment[] fragments = Enumerable.Range(0, 64).Select(index =>
            new LayerTestFragment(0, 0, .01f + index * .014f, 128u + (uint)index)).ToArray();
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(new(1, .25f, .5f), 1f / 128)];
        using var scene = new LayerCompositionTestScene(blockSize: 32, blockCapacity: 1);
        ulong originalVersion = scene.Graph.StructureVersion;
        ulong identity = 0;
        for (int index = 0; index <= 2; index++)
        {
            Assert.True(scene.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(20)));
            using (next)
            {
                ulong before = scene.Graph.StructureVersion;
                scene.Prepare.Update();
                ulong after = scene.Graph.StructureVersion;
                if (index == 0) Assert.Equal(before, after);
                if (index == 2) Assert.Equal(originalVersion + 1u, after);
                RenderGraphFrame frame = scene.Graph.BeginFrame(next);
                try
                {
                    Assert.Equal(after, frame.StructureVersion);
                    scene.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0)]);
                    identity = frame.FrameIdentity;
                    frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
                }
                finally { frame.Dispose(); }
            }
            scene.Wait(identity);
            if (index == 0)
            {
                Assert.Equal(identity, scene.Composition.Metrics.FrameIdentity);
                Assert.True(scene.Composition.Metrics.FragmentOverflow > 0);
            }
        }
        Assert.Equal(identity, scene.Composition.Metrics.FrameIdentity);
        Assert.Equal(0u, scene.Composition.Metrics.FragmentOverflow);
        Assert.Equal(64u, scene.Composition.Metrics.Fragments);
        Assert.InRange(Vector3.Distance(Reference(fragments, palette, LayerSortMode.DepthFrontToBack, Vector3.Zero), scene.Colors[0]), 0, 1f / 255);
    }

    [Fact]
    public void SixtyFourLayersUseTheSharedPathAndMatchPerFragmentComposition()
    {
        LayerTestFragment[] fragments = Enumerable.Range(0, 64).Select(index =>
            new LayerTestFragment(0, 0, .01f + index * .014f, 128u + (uint)index)).Reverse().ToArray();
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(new(1, .25f, .5f), 1f / 128)];
        Vector3 background = new(.1f, .2f, .3f);
        using var scene = new LayerCompositionTestScene(background: background);
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);
        Assert.InRange(Vector3.Distance(Reference(fragments, palette, LayerSortMode.DepthFrontToBack, background), scene.Colors[0]), 0, 1f / 255);
        Assert.Equal(64u, scene.Composition.Metrics.Fragments);
        Assert.Equal(64u, scene.Composition.Metrics.MaxFragmentsPerPixel);
        Assert.Equal(0u, scene.Composition.Metrics.SplitTiles);
        Assert.Equal(0u, scene.Composition.Metrics.SlowPathPixels);
    }

    [Fact]
    public void InterleavedTrianglesWithinOneClusterSortEveryFragmentByDepth()
    {
        LayerTestFragment[] fragments = Enumerable.Range(0, 33).Select(index =>
            new LayerTestFragment(0, 0, (index + .5f) / 33, 128u + (uint)index, Material: (uint)(index & 1))).Reverse().ToArray();
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(Vector3.UnitX, 1f / 32), LayerTestMaterial.Over(Vector3.UnitZ, 1f / 32)];
        using var scene = new LayerCompositionTestScene();
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
        Assert.InRange(Vector3.Distance(Reference(fragments, palette, LayerSortMode.DepthFrontToBack, Vector3.Zero), scene.Colors[0]), 0, 1f / 255);
        Assert.Equal(33u, scene.Composition.Metrics.Fragments);
    }

    [Fact]
    public void FourThousandAndNinetySixLayersComposeThroughTheSlowPath()
    {
        LayerTestFragment[] fragments = Enumerable.Range(0, 4096).Select(index =>
            new LayerTestFragment(0, 0, .0001f + index * .0002f, 128u + (uint)index)).Reverse().ToArray();
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(new(1, .25f, .5f), 1f / 2048)];
        using var scene = new LayerCompositionTestScene();
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);
        Assert.InRange(Vector3.Distance(Reference(fragments, palette, LayerSortMode.DepthFrontToBack, Vector3.Zero), scene.Colors[0]), 0, 1f / 255);
        Assert.Equal(4096u, scene.Composition.Metrics.Fragments);
        Assert.Equal(4096u, scene.Composition.Metrics.MaxFragmentsPerPixel);
        Assert.True(scene.Composition.Metrics.SlowPathPixels > 0);
    }

    [Theory]
    [InlineData(16u)]
    [InlineData(32u)]
    public void EightFullScreenLayersRemainExactAcrossTilePixelSegments(uint tileSize)
    {
        uint area = tileSize * tileSize;
        LayerTestFragment[] fragments = Enumerable.Range(0, (int)area * 8).Select(index =>
            new LayerTestFragment(0, (uint)index / 8, .1f + (index % 8) * .08f, 128u + (uint)(index % 8))).ToArray();
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(new(1, .25f, .5f), .125f)];
        using var scene = new LayerCompositionTestScene(tileSize, tileSize, tileSize, blockCapacity: 128);
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);
        Vector3 expected = Reference(fragments.Take(8), palette, LayerSortMode.DepthFrontToBack, Vector3.Zero);
        foreach (Vector3 color in scene.Colors) Assert.InRange(Vector3.Distance(expected, color), 0, 1f / 255);
        Assert.Equal(area * 8, scene.Composition.Metrics.Fragments);
        Assert.Equal(8u, scene.Composition.Metrics.MaxFragmentsPerPixel);
        if (tileSize == 32) Assert.True(scene.Composition.Metrics.SplitTiles > 0);
    }

    [Fact]
    public void DrawOrderPrecedesFullPrecisionDepth()
    {
        LayerTestFragment[] fragments = [new(0, 0, .8f, 128, DrawOrder: 0), new(0, 0, .2f, 256, Material: 1, DrawOrder: 1)];
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(Vector3.UnitX, .5f), LayerTestMaterial.Over(Vector3.UnitZ, .5f)];
        using var scene = new LayerCompositionTestScene(sortMode: LayerSortMode.DrawOrderThenDepth);
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
        Assert.InRange(Vector3.Distance(new(.25f, 0, .5f), scene.Colors[0]), 0, 1f / 255);
    }

    [Fact]
    public void DrawOrderGroupsApplyOverMultiplyAndAddInTheirDeclaredSequence()
    {
        LayerTestFragment[] fragments =
        [
            new(0, 0, .1f, 512, Material: 4, DrawOrder: 3),
            new(0, 0, .8f, 256, Material: 1, DrawOrder: 1),
            new(0, 0, .9f, 128, DrawOrder: 0),
            new(0, 0, .7f, 384, Material: 3, DrawOrder: 2),
            new(0, 0, .2f, 257, Material: 2, DrawOrder: 1),
        ];
        LayerTestMaterial[] palette =
        [
            LayerTestMaterial.Over(Vector3.UnitX, 1),
            LayerTestMaterial.Over(Vector3.UnitZ, .5f),
            LayerTestMaterial.Over(Vector3.UnitY, .5f),
            LayerTestMaterial.Over(new(.5f, .25f, .75f), 1),
            LayerTestMaterial.Over(new(.1f, .2f, .3f), 1),
        ];
        using var scene = new LayerCompositionTestScene(sortMode: LayerSortMode.DrawOrderThenDepth);
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette,
            [
                new LayerOperation { Opaque = true }, new LayerOperation(),
                new LayerOperation { Color = LayerColorOperation.Multiply, ColorWriteMask = ColorWriteMasks.Green | ColorWriteMasks.Blue },
                new LayerOperation { Color = LayerColorOperation.Add, ColorWriteMask = ColorWriteMasks.Green | ColorWriteMasks.Blue },
            ], [new(0), new(1), new(1), new(2), new(3)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);
        Vector3 expected = palette[2].Color + palette[2].Transmission *
            (palette[1].Color + palette[1].Transmission * palette[0].Color);
        Vector3 multiplied = expected * palette[3].Color;
        expected = new(expected.X, multiplied.Y + palette[4].Color.Y, multiplied.Z + palette[4].Color.Z);
        Assert.InRange(Vector3.Distance(expected, scene.Colors[0]), 0, 1f / 255);
    }

    [Fact]
    public void EqualDrawOrdersMatchFrontToBackDepthComposition()
    {
        LayerTestFragment[] fragments = [new(0, 0, .8f, 128, DrawOrder: 7), new(0, 0, .2f, 256, Material: 1, DrawOrder: 7)];
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(Vector3.UnitX, .5f), LayerTestMaterial.Over(Vector3.UnitZ, .5f)];
        Vector3 expected;
        using (var depth = new LayerCompositionTestScene())
        {
            RenderGraphFrame frame = depth.BeginFrame();
            ulong identity;
            try
            {
                depth.Record(ref frame, fragments, palette, [new LayerOperation()], [new(0), new(0)]);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[depth.Graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            depth.Wait(identity);
            expected = depth.Colors[0];
        }
        using var ordered = new LayerCompositionTestScene(sortMode: LayerSortMode.DrawOrderThenDepth);
        RenderGraphFrame orderedFrame = ordered.BeginFrame();
        ulong orderedIdentity;
        try
        {
            ordered.Record(ref orderedFrame, fragments, palette, [new LayerOperation()], [new(0), new(0)]);
            orderedIdentity = orderedFrame.FrameIdentity;
            orderedFrame.Execute(new QueueCompletion[ordered.Graph.MaximumQueueCompletionCount]);
        }
        finally { orderedFrame.Dispose(); }
        ordered.Wait(orderedIdentity);
        Assert.Equal(expected, ordered.Colors[0]);
    }

    [Fact]
    public void ARemoteOpaqueLayerPreservesLaterNearLayerContributions()
    {
        LayerTestFragment[] fragments = [new(0, 0, .8f, 128), new(0, 0, .2f, 256, Material: 1, DrawOrder: 1)];
        LayerTestMaterial[] palette = [LayerTestMaterial.Over(Vector3.UnitX, 1), LayerTestMaterial.Over(Vector3.UnitZ, .5f)];
        using var scene = new LayerCompositionTestScene(sortMode: LayerSortMode.DepthBackToFront);
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
        Assert.InRange(Vector3.Distance(new(.5f, 0, .5f), scene.Colors[0]), 0, 1f / 255);
    }

    [Theory]
    [InlineData(LayerColorOperation.Multiply)]
    [InlineData(LayerColorOperation.Min)]
    [InlineData(LayerColorOperation.Max)]
    public void FrontContributionsRemainOutsideOperationsOnTheBackground(LayerColorOperation operation)
    {
        Vector3 background = new(2, -1, 3);
        LayerTestFragment[] fragments = [new(0, 0, .1f, 128), new(0, 0, .6f, 256, Material: 1)];
        LayerTestMaterial[] palette = [new(new(.2f, .1f, .3f, 0), new(.5f, .5f, .5f, 0)),
            new(new(4, -2, 5, 0), new(.5f, 0, .75f, 0))];
        using var scene = new LayerCompositionTestScene(background: background);
        RenderGraphFrame frame = scene.BeginFrame();
        ulong identity;
        try
        {
            scene.Record(ref frame, fragments, palette,
                [new LayerOperation(), new LayerOperation { Color = operation }], [new(0), new(1)]);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[scene.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        scene.Wait(identity);
        Vector3 behind = operation switch
        {
            LayerColorOperation.Min => Vector3.Min(background, palette[1].Color),
            LayerColorOperation.Max => Vector3.Max(background, palette[1].Color),
            _ => background * (palette[1].Color + palette[1].Transmission),
        };
        Vector3 expected = palette[0].Color + palette[0].Transmission * behind;
        Assert.InRange(Vector3.Distance(expected, scene.Colors[0]), 0, 1f / 255);
    }

    private static Vector3 Reference(IEnumerable<LayerTestFragment> fragments, IReadOnlyList<LayerTestMaterial> palette,
        LayerSortMode mode, Vector3 background)
    {
        IEnumerable<LayerTestFragment> ordered = mode switch
        {
            LayerSortMode.DepthBackToFront => fragments.OrderByDescending(value => value.Depth).ThenBy(value => value.Identity),
            LayerSortMode.DrawOrderThenDepth => fragments.OrderBy(value => value.DrawOrder).ThenBy(value => value.Depth).ThenBy(value => value.Identity),
            _ => fragments.OrderBy(value => value.Depth).ThenBy(value => value.Identity),
        };
        if (mode != LayerSortMode.DepthBackToFront) ordered = ordered.Reverse();
        Vector3 result = background;
        foreach (LayerTestFragment fragment in ordered)
        {
            LayerTestMaterial material = palette[(int)fragment.Material];
            result = material.Color + material.Transmission * result;
        }
        return result;
    }
}

using System.Numerics;
using SomeEngine.Transparency.Research;

namespace SomeEngine.Render.Tests;

public sealed class TileReferenceTests
{
    [Fact]
    public void TileReferencesKeepInputOrderAndTriangleAssociationRanges()
    {
        // These three projected triangles are a hand-worked tile coverage example.
        ProjectedTriangle[] source =
        [
            new(new(-1, -1, .25f, 1), new(0, -1, .25f, 1), new(-1, 1, .25f, 1), new(1, 0, 0, .5f), 17),
            new(new(0, -1, .5f, 1), new(1, -1, .5f, 1), new(1, 0, .5f, 1), new(0, 1, 0, .5f), 9),
            new(new(-1, .1f, .75f, 1), new(-.5f, .1f, .75f, 1), new(-1, 1, .75f, 1), new(0, 0, 1, .5f), 3),
        ];
        var geometry = new TileVisibilityGeometry(new TriangleHierarchy(source), 48, 32);

        Assert.Equal(3, geometry.TileColumns);
        Assert.Equal(2, geometry.TileRows);
        Assert.Equal([0u, 2u, 0u, 0u, 0u, 1u, 1u], geometry.Associations);
        Assert.Equal([new TileRange(0, 2), new(2, 1), new(3, 0), new(3, 1), new(4, 2), new(6, 1)], geometry.Ranges);
        Assert.Equal([0u, 4u, 6u], geometry.Triangles.Select(triangle => triangle.FirstAssociation));
        Assert.Equal([17u, 9u, 3u], geometry.Triangles.Select(triangle => triangle.TriangleId));
        Assert.Equal([new Vector3(0, 0, .25f), new(0, 0, .5f), new(0, 0, .75f)],
            geometry.Triangles.Select(triangle => triangle.DepthPlane));
    }
    [Fact]
    public void BlendReferencesKeepSourceOrderAcrossWords()
    {
        // Seventeen two-triangle planes cross all four tiles: each tile consumes 32 + 2 references.
        var data = new BlendEffectData(BlendEffect.Xray, 17, 32, 32);

        Assert.Equal(34, data.Triangles.Length);
        Assert.Equal(136, data.References.Length);
        Assert.Equal([new BlendRange(0, 2), new(2, 2), new(4, 2), new(6, 2)], data.TileWords);
        Assert.Equal([new BlendWord(0, 32), new(32, 2), new(34, 32), new(66, 2),
            new(68, 32), new(100, 2), new(102, 32), new(134, 2)], data.Words);
        for (int tile = 0; tile < 4; tile++)
            Assert.Equal(Enumerable.Range(0, 34).Select(index => (uint)index), data.References.AsSpan(tile * 34, 34).ToArray());
    }}
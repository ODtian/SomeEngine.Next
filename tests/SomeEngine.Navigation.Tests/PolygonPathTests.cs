using System.Numerics;

namespace SomeEngine.Navigation.Tests;

public sealed class PolygonPathTests
{
    [Fact]
    public void APathRequestContinuesAcrossSearchBudgets()
    {
        NavigationMesh mesh = StraightCorridor();
        var input = new NavigationSearchInput<NavigationMesh>(mesh, Profile(), new NavigationPolicy(),
            new(0.5f, 0, 1), new(5.5f, 0, 1));
        PolygonSearchState request = PolygonNavigationSearch.Create(in input);

        NavigationSearchStep first = PolygonNavigationSearch.Advance(request, 1);
        NavigationSearchStep completed = PolygonNavigationSearch.Advance(request, 16);

        Assert.Equal(NavigationSearchStatus.Searching, first.Status);
        Assert.Equal(NavigationSearchStatus.Found, completed.Status);
        Assert.Equal(input.Goal, completed.Path!.Corners.Span[^1]);
    }

    [Fact]
    public void PathCornersFollowTheInsideBendOfItsCorridor()
    {
        var input = new NavigationSearchInput<NavigationMesh>(BentCorridor(), Profile(), new NavigationPolicy(),
            new(1, 0, 1), new(7, 0, 1));
        PolygonSearchState request = PolygonNavigationSearch.Create(in input);

        NavigationPath path = PolygonNavigationSearch.Advance(request, 32).Path!;

        Assert.Equal(new Vector3[] { new(1, 0, 1), new(2, 0, 4), new(6, 0, 4), new(7, 0, 1) },
            path.Corners.ToArray());
    }

    [Fact]
    public void AreaCostsCanSelectALongerCorridor()
    {
        var policy = new NavigationPolicy([new(1, 10)]);
        var input = new NavigationSearchInput<NavigationMesh>(TwoCorridors(), Profile(), policy,
            new(1, 0, 1.5f), new(5, 0, 1.5f));
        PolygonSearchState request = PolygonNavigationSearch.Create(in input);

        NavigationPath path = PolygonNavigationSearch.Advance(request, 32).Path!;

        Assert.Equal(new Vector3[] { new(1, 0, 1.5f), new(2, 0, 5), new(4, 0, 5), new(5, 0, 1.5f) },
            path.Corners.ToArray());
    }

    [Fact]
    public void DetailTrianglesProvideTheHeightInsideAPolygon()
    {
        var mesh = new NavigationMesh(
            [new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), new(4, 0, 0)],
            [0, 1, 2, 3], [new(0, 4, 0, 0, DetailCount: 4)], [],
            [new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), new(4, 0, 0), new(2, 2, 2)],
            [new(0, 1, 4), new(1, 2, 4), new(2, 3, 4), new(3, 0, 4)]);

        bool sampled = mesh.TrySampleHeight(0, new(1, 0, 1), out float height);

        Assert.True(sampled);
        Assert.Equal(1f, height);
    }

    private static NavigationProfile Profile() => new(
        [new(-0.2f, -0.2f), new(-0.2f, 0.2f), new(0.2f, 0.2f), new(0.2f, -0.2f)], 1.7f);

    private static NavigationMesh StraightCorridor() => new(
        [new(0, 0, 0), new(0, 0, 2), new(2, 0, 0), new(2, 0, 2),
         new(4, 0, 0), new(4, 0, 2), new(6, 0, 0), new(6, 0, 2)],
        [0, 1, 3, 2, 2, 3, 5, 4, 4, 5, 7, 6],
        [new(0, 4, 0, 1), new(4, 4, 1, 2), new(8, 4, 3, 1)],
        [new(1, new(2, 0, 2), new(2, 0, 0)),
         new(0, new(2, 0, 0), new(2, 0, 2)),
         new(2, new(4, 0, 2), new(4, 0, 0)),
         new(1, new(4, 0, 0), new(4, 0, 2))]);

    private static NavigationMesh BentCorridor() => new(
        [new(0, 0, 0), new(0, 0, 6), new(2, 0, 6), new(2, 0, 0),
         new(2, 0, 4), new(2, 0, 6), new(6, 0, 6), new(6, 0, 4),
         new(6, 0, 0), new(6, 0, 6), new(8, 0, 6), new(8, 0, 0)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [new(0, 4, 0, 1), new(4, 4, 1, 2), new(8, 4, 3, 1)],
        [new(1, new(2, 0, 6), new(2, 0, 4)),
         new(0, new(2, 0, 4), new(2, 0, 6)),
         new(2, new(6, 0, 6), new(6, 0, 4)),
         new(1, new(6, 0, 4), new(6, 0, 6))]);

    private static NavigationMesh TwoCorridors() => new(
        [new(0, 0, 0), new(0, 0, 6), new(2, 0, 6), new(2, 0, 0),
         new(2, 0, 1), new(2, 0, 2), new(4, 0, 2), new(4, 0, 1),
         new(4, 0, 0), new(4, 0, 6), new(6, 0, 6), new(6, 0, 0),
         new(2, 0, 5), new(2, 0, 6), new(4, 0, 6), new(4, 0, 5)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [new(0, 4, 0, 2), new(4, 4, 2, 2, Area: 1),
         new(8, 4, 4, 2), new(12, 4, 6, 2, Area: 2)],
        [new(1, new(2, 0, 2), new(2, 0, 1)),
         new(3, new(2, 0, 6), new(2, 0, 5)),
         new(0, new(2, 0, 1), new(2, 0, 2)),
         new(2, new(4, 0, 2), new(4, 0, 1)),
         new(1, new(4, 0, 1), new(4, 0, 2)),
         new(3, new(4, 0, 5), new(4, 0, 6)),
         new(0, new(2, 0, 5), new(2, 0, 6)),
         new(2, new(4, 0, 6), new(4, 0, 5))]);
}

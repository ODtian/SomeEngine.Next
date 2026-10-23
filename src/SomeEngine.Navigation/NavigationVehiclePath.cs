namespace SomeEngine.Navigation;

internal static class NavigationVehiclePath
{
    internal static NavigationPath Create(in NavigationSearchInput<NavigationVehicleSpace> input,
        NavigationMotionSegment[] segments, float cost)
    {
        var poses = new List<NavigationPose> { new(input.Start, input.StartHeading) };
        var tiles = new HashSet<NavigationTileCoordinate>();
        foreach (NavigationMotionSegment segment in segments)
        {
            int count = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(segment.Control.Speed) * segment.Control.Duration /
                (input.Space.Grid.CellSize / 4)));
            for (int sample = 1; sample <= count; sample++)
            {
                NavigationPose pose = input.Space.Motions.Model.Integrate(segment.Start, segment.Control, segment.Control.Duration * sample / count);
                int cell = input.Space.Grid.FindCell(pose.Position);
                NavigationGridCell ground = input.Space.Grid.Cells.Span[cell];
                poses.Add(pose with { Position = pose.Position with { Y = ground.Position.Y } });
                tiles.Add(ground.Tile);
            }
        }
        ulong contentVersion = input.Space.Grid.ContentVersion;
        NavigationPathDependency[] dependencies = tiles.Select(tile => new NavigationPathDependency(tile, contentVersion)).ToArray();
        return new(Array.Empty<int>(), poses.Select(pose => pose.Position).ToArray(), dependencies, cost,
            poses: poses.ToArray(), motionSegments: segments);
    }
}

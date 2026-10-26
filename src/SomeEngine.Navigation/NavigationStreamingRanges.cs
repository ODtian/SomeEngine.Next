using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

internal readonly record struct NavigationInterestData(Vector3 Position, float Radius, Vector3 Forward,
    float LookAheadDistance, NavigationPath? Path);

internal readonly record struct NavigationInterestTileRange(int DirectoryIndex, List<NavigationTileCoordinate>[] TilesByInterest);

internal interface INavigationTileBounds<TTile>
{
    NavigationBounds GetBounds(TTile tile);
    void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses);
}

internal static class NavigationStreamingRanges
{
    private const int TilesPerRange = 128;
    private readonly record struct TileRange(int DirectoryIndex, int Start, int Count);

    internal static NavigationInterestData[] CaptureInterests(IList<NavigationInterest> interests)
    {
        var values = new NavigationInterestData[interests.Count];
        for (int index = 0; index < values.Length; index++)
        {
            NavigationInterest interest = interests[index];
            values[index] = new(interest.Position, interest.Radius,
                interest.Position + interest.Direction * interest.LookAheadDistance, interest.LookAheadDistance, interest.Path);
        }
        return values;
    }

    internal static NavigationInterestTileRange[] CollectTiles(Scheduler scheduler,
        ReadOnlyMemory<NavigationRegionTile>[] directories, NavigationInterestData[] interests)
    {
        if (interests.Length == 0) return [];
        var ranges = new List<TileRange>();
        var accesses = new List<JobResourceAccess>();
        for (int directory = 0; directory < directories.Length; directory++)
        {
            accesses.Add(NavigationJobAccess.Read(scheduler, directories[directory]));
            for (int start = 0; start < directories[directory].Length; start += TilesPerRange)
                ranges.Add(new(directory, start, Math.Min(TilesPerRange, directories[directory].Length - start)));
        }
        var output = new NavigationInterestTileRange[ranges.Count];
        scheduler.ExecuteParallel(new CollectTilesJob(directories, interests, ranges.ToArray(), output), ranges.Count,
            JobScheduleOptions.AutomaticBatchSize, accesses.ToArray());
        return output;
    }

    private readonly struct CollectTilesJob(ReadOnlyMemory<NavigationRegionTile>[] directories,
        NavigationInterestData[] interests, TileRange[] ranges, NavigationInterestTileRange[] output) : IJobParallelFor
    {
        public void Execute(int index)
        {
            TileRange range = ranges[index];
            ReadOnlySpan<NavigationRegionTile> tiles = directories[range.DirectoryIndex].Span.Slice(range.Start, range.Count);
            var lists = new List<NavigationTileCoordinate>[interests.Length];
            for (int item = 0; item < interests.Length; item++)
            {
                NavigationInterestData interest = interests[item];
                var matched = new List<NavigationTileCoordinate>();
                foreach (NavigationRegionTile tile in tiles)
                    if (Distance(interest.Position, tile.Bounds) <= interest.Radius ||
                        Distance(interest.Forward, tile.Bounds) <= interest.Radius) matched.Add(tile.Tile);
                lists[item] = matched;
            }
            output[index] = new(range.DirectoryIndex, lists);
        }
    }

    internal static List<JobResourceAccess> CollectPathReads(Scheduler scheduler, NavigationInterestData[] interests)
    {
        var accesses = new List<JobResourceAccess>();
        foreach (NavigationInterestData interest in interests)
        {
            if (interest.Path is not { } path) continue;
            if (!path.RequiredTiles.IsEmpty) accesses.Add(NavigationJobAccess.Read(scheduler, path.RequiredTiles));
            if (path.RegionRoute is { } route) accesses.Add(NavigationJobAccess.Read(scheduler, route.Entrances));
        }
        return accesses;
    }

    internal static Dictionary<TTile, float> CalculateDistances<TTile, TBounds>(Scheduler scheduler, TTile[] tiles,
        NavigationInterestData[] interests, TBounds bounds, float emptyInterestDistance = float.PositiveInfinity)
        where TTile : notnull
        where TBounds : struct, INavigationTileBounds<TTile>
    {
        var values = new float[tiles.Length];
        var accesses = new List<JobResourceAccess>();
        bounds.AddReadAccesses(scheduler, accesses);
        scheduler.ExecuteParallel(new DistanceJob<TTile, TBounds>(tiles, interests, bounds, values, emptyInterestDistance),
            tiles.Length, JobScheduleOptions.AutomaticBatchSize, accesses.ToArray());
        var distances = new Dictionary<TTile, float>(tiles.Length);
        for (int index = 0; index < tiles.Length; index++) distances.Add(tiles[index], values[index]);
        return distances;
    }

    private readonly struct DistanceJob<TTile, TBounds>(TTile[] tiles, NavigationInterestData[] interests,
        TBounds source, float[] values, float emptyInterestDistance) : IJobParallelFor
        where TTile : notnull
        where TBounds : struct, INavigationTileBounds<TTile>
    {
        public void Execute(int index)
        {
            NavigationBounds bounds = source.GetBounds(tiles[index]);
            float distance = interests.Length == 0 ? emptyInterestDistance : float.PositiveInfinity;
            foreach (NavigationInterestData interest in interests)
                distance = MathF.Min(distance, Distance(interest.Position, bounds));
            values[index] = distance;
        }
    }

    internal static float Distance(Vector3 point, NavigationBounds bounds) =>
        Vector3.Distance(point, Vector3.Clamp(point, bounds.Minimum, bounds.Maximum));
}

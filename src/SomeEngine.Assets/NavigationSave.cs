using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

public sealed record NavigationSaveResult(World World, NavigationSaveResources Resources);

/// <summary>Durable resource provenance and navigation intent followed by the game's existing durable World payload.</summary>
public static partial class NavigationSave
{
    private static readonly SerializationRegistry TableRegistry = new SerializationRegistry().Register<NavigationSavedTable, NavigationSaveCodec>(
        new(Guid.Parse("35e686ea-57c6-4441-b691-d6e3493d1fa0"), "SomeEngine.Navigation.Save", 1));

    /// <summary>Call after actual movement, input changes and navigation Apply have completed for the save boundary.</summary>
    public static void Write(Stream stream, World world, SerializationRegistry gameRegistry, NavigationSaveResources resources,
        SerializeOptions options = default)
    {
        var capture = new NavigationSaveResourceCapture(resources);
        capture.CaptureRegions();
        var actors = new List<NavigationSavedActor>();
        QueryHandle query = world.Query(world.QueryDefinition().Read<NavigationAgent>()
            .Optional<NavigationGoal>(QueryAccess.Read).Optional<FlowFollower>(QueryAccess.Read)
            .Optional<PathNavigation<NavigationMesh>>(QueryAccess.Read).Optional<PathNavigation<HeadingNavigationMesh>>(QueryAccess.Read)
            .Optional<PathNavigation<NavigationVehicleSpace>>(QueryAccess.Read)
            .Optional<PathNavigation<NavigationScene>>(QueryAccess.Read).Optional<PathNavigation<HeadingNavigationScene>>(QueryAccess.Read));
        try
        {
            world.ExecuteReadSnapshot(query, cursor =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    NavigationAgent agent = row.Read<NavigationAgent>();
                    row.TryRead(out NavigationGoal goal);
                    int resource = -1;
                    SavedPathKind kind = SavedPathKind.None;
                    bool enabled = true;
                    if (row.TryRead(out FlowFollower follower))
                    {
                        kind = SavedPathKind.Flow;
                        resource = capture.Add(follower.Flow);
                        enabled = world.IsEnabled<FlowFollower>(row.Entity);
                    }
                    else if (row.TryRead(out PathNavigation<NavigationMesh> mesh))
                    {
                        kind = NavigationComponents.HasPath<NavigationMesh, PolyanyaSearchState>(world, row.Entity) ? SavedPathKind.Polyanya : SavedPathKind.Polygon;
                        resource = capture.Add(mesh.Space);
                    }
                    else if (row.TryRead(out PathNavigation<HeadingNavigationMesh> heading))
                    {
                        kind = SavedPathKind.Asa;
                        resource = capture.Add(heading.Space);
                    }
                    else if (row.TryRead(out PathNavigation<NavigationVehicleSpace> vehicle))
                    {
                        kind = NavigationComponents.HasPath<NavigationVehicleSpace, HybridAStarSearchState>(world, row.Entity) ? SavedPathKind.HybridAStar : SavedPathKind.StateLattice;
                        resource = capture.Add(vehicle.Space);
                    }
                    else if (row.TryRead(out PathNavigation<NavigationScene> scene))
                    {
                        kind = NavigationComponents.HasPath<NavigationScene, DStarLiteSearchState>(world, row.Entity) ? SavedPathKind.DStarLite : SavedPathKind.Scene;
                        resource = capture.Add(scene.Space);
                    }
                    else if (row.TryRead(out PathNavigation<HeadingNavigationScene> headingScene))
                    {
                        kind = SavedPathKind.HeadingScene;
                        resource = capture.Add(headingScene.Space);
                    }
                    if (kind is not SavedPathKind.None and not SavedPathKind.Flow) enabled = world.IsEnabled<NavigationGoal>(row.Entity);
                    actors.Add(new(row.Entity, capture.Add(agent.Profile), capture.Add(agent.Policy), agent.DesiredSpeed, kind, resource, goal, enabled));
                }
            });
        }
        finally { world.ReleaseQuery(query); }
        NavigationSavedSource[] sourceBindings = CaptureSources(world, capture);
        var table = new NavigationSavedTable(capture.Records.ToArray(), actors.ToArray(), sourceBindings, capture.Regions.ToArray());
        WorldSerializer.WriteDurableComponent(stream, in table, TableRegistry);
        WorldSerializer.WriteDurableWorld(stream, world, gameRegistry, options);
    }
}

internal enum SavedPathKind : byte { None, Flow, Polygon, Polyanya, Asa, StateLattice, HybridAStar, Scene, DStarLite, HeadingScene }
internal readonly record struct NavigationSavedActor(Entity Entity, int Profile, int Policy, float DesiredSpeed,
    SavedPathKind Kind, int Resource, NavigationGoal Goal, bool Enabled);
internal readonly record struct NavigationSavedTable(NavigationSavedResource[] Resources, NavigationSavedActor[] Actors,
    NavigationSavedSource[] Sources, NavigationSavedRegion[] Regions) : IComponent;

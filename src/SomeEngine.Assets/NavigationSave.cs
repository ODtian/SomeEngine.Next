using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
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
        Query query = world.Query(new QueryDescription().WithStored<NavigationAgent>()
            .Optional<NavigationGoal>().Optional<FlowFollower>()
            .Optional<PathNavigation<NavigationMesh>>().Optional<PathNavigation<HeadingNavigationMesh>>()
            .Optional<PathNavigation<NavigationVehicleSpace>>()
            .Optional<PathNavigation<NavigationScene>>().Optional<PathNavigation<HeadingNavigationScene>>());
        {
            {
                using QueryScope cursor = query.OpenRead();
                foreach (QueryChunk chunk in cursor)
                {
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        NavigationGoal goal = chunk.Optional<NavigationGoal>(rowIndex).Value;
                        FlowFollower follower = chunk.Optional<FlowFollower>(rowIndex).Value;
                        PathNavigation<NavigationMesh> mesh = chunk.Optional<PathNavigation<NavigationMesh>>(rowIndex).Value;
                        PathNavigation<HeadingNavigationMesh> heading = chunk.Optional<PathNavigation<HeadingNavigationMesh>>(rowIndex).Value;
                        PathNavigation<NavigationVehicleSpace> vehicle = chunk.Optional<PathNavigation<NavigationVehicleSpace>>(rowIndex).Value;
                        PathNavigation<NavigationScene> scene = chunk.Optional<PathNavigation<NavigationScene>>(rowIndex).Value;
                        PathNavigation<HeadingNavigationScene> headingScene = chunk.Optional<PathNavigation<HeadingNavigationScene>>(rowIndex).Value;

                        NavigationAgent agent = chunk.Read<NavigationAgent>()[rowIndex];

                        int resource = -1;
                        SavedPathKind kind = SavedPathKind.None;
                        bool enabled = true;
                        if (chunk.Has<FlowFollower>(rowIndex))
                        {
                            kind = SavedPathKind.Flow;
                            resource = capture.Add(follower.Flow);
                            enabled = world.IsEnabled<FlowFollower>(chunk.Entities[rowIndex]);
                        }
                        else if (chunk.Has<PathNavigation<NavigationMesh>>(rowIndex))
                        {
                            kind = NavigationComponents.HasPath<NavigationMesh, PolyanyaSearchState>(world, chunk.Entities[rowIndex]) ? SavedPathKind.Polyanya : SavedPathKind.Polygon;
                            resource = capture.Add(mesh.Space);
                        }
                        else if (chunk.Has<PathNavigation<HeadingNavigationMesh>>(rowIndex))
                        {
                            kind = SavedPathKind.Asa;
                            resource = capture.Add(heading.Space);
                        }
                        else if (chunk.Has<PathNavigation<NavigationVehicleSpace>>(rowIndex))
                        {
                            kind = NavigationComponents.HasPath<NavigationVehicleSpace, HybridAStarSearchState>(world, chunk.Entities[rowIndex]) ? SavedPathKind.HybridAStar : SavedPathKind.StateLattice;
                            resource = capture.Add(vehicle.Space);
                        }
                        else if (chunk.Has<PathNavigation<NavigationScene>>(rowIndex))
                        {
                            kind = NavigationComponents.HasPath<NavigationScene, DStarLiteSearchState>(world, chunk.Entities[rowIndex]) ? SavedPathKind.DStarLite : SavedPathKind.Scene;
                            resource = capture.Add(scene.Space);
                        }
                        else if (chunk.Has<PathNavigation<HeadingNavigationScene>>(rowIndex))
                        {
                            kind = SavedPathKind.HeadingScene;
                            resource = capture.Add(headingScene.Space);
                        }
                        if (kind is not SavedPathKind.None and not SavedPathKind.Flow) enabled = world.IsEnabled<NavigationGoal>(chunk.Entities[rowIndex]);
                        actors.Add(new(chunk.Entities[rowIndex], capture.Add(agent.Profile), capture.Add(agent.Policy), agent.DesiredSpeed, kind, resource, goal, enabled));

                    }
                }

            }
        }
        for (int index = 0; index < actors.Count; index++)
            actors[index] = actors[index] with { Traversal = CaptureTraversal(world, actors[index]), Controls = CaptureControls(world, actors[index].Entity, capture) };
        NavigationSavedSource[] sourceBindings = CaptureSources(world, capture);
        var table = new NavigationSavedTable(capture.Records.ToArray(), actors.ToArray(), sourceBindings, capture.Regions.ToArray(), CaptureCrowd(world));
        WorldSerializer.WriteComponent(stream, in table, TableRegistry, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        new WorldSerializer(gameRegistry).Save(world, stream, options with { Purpose = SavePurpose.DurableSave });
    }
}

internal enum SavedPathKind : byte { None, Flow, Polygon, Polyanya, Asa, StateLattice, HybridAStar, Scene, DStarLite, HeadingScene }
internal readonly record struct NavigationSavedActor(Entity Entity, int Profile, int Policy, float DesiredSpeed,
    SavedPathKind Kind, int Resource, NavigationGoal Goal, bool Enabled, NavigationTraversalIntent Traversal = default,
    NavigationSavedControls Controls = default);
internal readonly record struct NavigationSavedTable(NavigationSavedResource[] Resources, NavigationSavedActor[] Actors,
    NavigationSavedSource[] Sources, NavigationSavedRegion[] Regions, NavigationSavedCrowd[] Crowd) : IComponent;

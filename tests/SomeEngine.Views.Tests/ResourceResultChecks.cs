using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Views.Tests;

public static partial class ResourceResultChecks
{
    public static void Verify(bool jobWorld)
    {
        using var frames = new HeadlessFrameDriver(new World(execution:
            jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null));
        var source = new State<int>(1);
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new ResourceContent(source, target));
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 10,
            "The synchronous resource commits its first result through the component copy.");
        source.Value = 2;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 20,
            "The retained resource commits the new source result through the same calculation.");
        Require(!frames.World.HasSparse<BuildError>(root), "The current resource completes successfully.");
    }

    private readonly record struct ResourceContent(State<int> Source, Entity Target) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            State<int> source = Source;
            Entity target = Target;
            Scope.Compute(() =>
            {
                Resource<int> resource = Scope.Resource(source.Value,
                    static (value, cancel) => ValueTask.FromResult(value * 10), 1);
                Scope.Write<Number>(target) = new Number(resource.Value);
            });
            return default;
        }
    }

    private readonly record struct Number(int Value) : IComponent;

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

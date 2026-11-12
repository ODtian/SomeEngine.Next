using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

internal unsafe struct GraphRelease : IBufferElement
{
    internal delegate* managed<World, Entity, void> Function;
}

internal static unsafe class GraphReleases
{
    internal static void Add(World world, Entity animation, delegate* managed<World, Entity, void> function)
    {
        if (!world.Has<Buffer<GraphRelease>>(animation))
            world.AddBuffer<GraphRelease>(animation);
        var release = new GraphRelease { Function = function };
        {
            using var releases = world.GetBuffer<GraphRelease>(animation);
            Visit(releases, ref release);

            static void Visit(DynamicBuffer<GraphRelease> releases, ref GraphRelease release)
            {
                foreach (GraphRelease previous in releases.AsSpan())
                    if ((nint)previous.Function == (nint)release.Function)
                        return;
                releases.Add(release);

            }
        }
    }

    internal static void Release(World world, Entity animation)
    {
        if (!world.Has<Buffer<GraphRelease>>(animation))
            return;
        int count = PropertyAnimation.BufferCount<GraphRelease>(world, animation);
        for (int index = 0; index < count; index++)
            PropertyAnimation.ReadBufferItem<GraphRelease>(world, animation, index).Function(world, animation);
        {
            using var releases = world.GetBuffer<GraphRelease>(animation);
            releases.Clear();
        }
    }
}

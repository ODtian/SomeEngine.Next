using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>Actual ECS storage for one complete value, operations, parameter, state, and evaluator combination.</summary>
[BufferCapacity(1)]
public struct MotionInstance<T, TOps, TParameters, TState, TEvaluator> : IBufferElement
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    public MotionData<T, TParameters, TState> Data;
}

/// <summary>The target holds the relationship to each declared source output.</summary>
[BufferCapacity(1)]
public struct PropertySource<T> : IBufferElement where T : unmanaged
{
    public Channel<T> Channel;
    public byte Layer;
    public Entity Animation;
    public int Track;
    public int BaseIndex;
    public T Value, Reference;
    public TrackEndpoints Endpoints;
    public bool Active, Dirty, Selected, Direct, HadDynamic;
}

[BufferCapacity(1)]
internal struct MotionDeclaration<T> : IBufferElement where T : unmanaged
{
    internal Channel<T> Channel;
    internal byte Layer;
    internal int Type, Capacity;
}

[BufferCapacity(1)]
internal unsafe struct MotionStop : IBufferElement
{
    internal int Type;
    internal int ValueType;
    internal delegate* managed<World, Entity, void> Stop;
}

internal readonly unsafe struct MotionAccess<T>(
    delegate* managed<World, Entity, int, in MotionValues<T>, void> retarget,
    delegate* managed<World, Entity, int, void> release) where T : unmanaged
{
    internal readonly delegate* managed<World, Entity, int, in MotionValues<T>, void> Retarget = retarget;
    internal readonly delegate* managed<World, Entity, int, void> Release = release;
}

internal static class MotionAccesses<T> where T : unmanaged
{
    internal static MotionAccess<T>[] Items = new MotionAccess<T>[16];
    private static int _count;
    private static readonly object Gate = new();
    internal static int Add(MotionAccess<T> access)
    {
        lock (Gate)
        {
            if (_count == Items.Length) Array.Resize(ref Items, _count * 2);
            Items[_count] = access;
            return ++_count;
        }
    }
}

internal static unsafe class MotionStorage<T, TOps, TParameters, TState, TEvaluator>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    internal static readonly int Type = MotionAccesses<T>.Add(new(&Retarget, &Release));
    internal static readonly delegate* managed<World, Entity, void> StopFunction = &Stop;

    internal static Span<MotionData<T, TParameters, TState>> Data(
        DynamicBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>> instances)
    {
        Span<MotionInstance<T, TOps, TParameters, TState, TEvaluator>> values = instances.AsSpan();
        return values.IsEmpty ? default : MemoryMarshal.CreateSpan(ref values[0].Data, values.Length);
    }

    private static void Retarget(World world, Entity animation, int index, in MotionValues<T> values)
    {
        var update = (Index: index, Values: values);
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, (int Index, MotionValues<T> Values)>(
            animation, ref update, static (instances, ref update) =>
            {
                ref MotionData<T, TParameters, TState> data = ref instances[update.Index].Data;
                TEvaluator.Retarget(in data.Parameters, in data.Parameters, in update.Values, ref data.State);
                data.Values = update.Values;
            });
    }

    private static void Release(World world, Entity animation, int index)
    {
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, int>(
            animation, ref index, static (instances, ref index) =>
            {
                ref MotionData<T, TParameters, TState> data = ref instances[index].Data;
                TEvaluator.Release(ref data.State);
                data.Active = false;
            });
    }

    private static void Stop(World world, Entity animation)
    {
        var stopping = (World: world, Animation: animation);
        world.ExecuteBufferWrite<Track<T>, (World World, Entity Animation)>(animation, ref stopping, static (tracks, ref stopping) =>
        {
            for (int index = 0; index < tracks.Count; index++)
            {
                ref Track<T> track = ref tracks[index];
                if (track.MotionType != Type) continue;
                if (track.MotionInitialized) Release(stopping.World, stopping.Animation, track.MotionIndex);
                track.MotionInitialized = false;
                track.Finished = true;
            }
        });
    }
}

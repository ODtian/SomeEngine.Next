using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    private readonly unsafe struct DefaultFunctions(
        delegate* managed<World, Entity, void> declare,
        delegate* managed<World, Entity, ref byte, ref byte, Spec, byte, TrackEndpoints, Entity> start)
    {
        internal readonly delegate* managed<World, Entity, void> Declare = declare;
        internal readonly delegate* managed<World, Entity, ref byte, ref byte, Spec, byte, TrackEndpoints, Entity> Start = start;
    }

    private static unsafe class DefaultProperties<T> where T : unmanaged
    {
        private static readonly DefaultFunctions Functions = Resolve();
        internal static bool Declare(World world, Entity target)
        {
            if (Functions.Declare == null) return false;
            Functions.Declare(world, target);
            return true;
        }

        internal static bool TryStart(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints, out Entity animation)
        {
            animation = Entity.Null;
            if (Functions.Start == null) return false;
            animation = Functions.Start(world, target, ref Unsafe.As<Channel<T>, byte>(ref channel), ref Unsafe.As<T, byte>(ref value), spec, layer, endpoints);
            return true;
        }

        private static DefaultFunctions Resolve()
        {
            if (typeof(T) == typeof(float)) return For<float, FloatOps>();
            if (typeof(T) == typeof(Vector2)) return For<Vector2, Vector2Ops>();
            if (typeof(T) == typeof(Vector3)) return For<Vector3, Vector3Ops>();
            if (typeof(T) == typeof(Vector4)) return For<Vector4, Vector4Ops>();
            if (typeof(T) == typeof(Quaternion)) return For<Quaternion, QuaternionOps>();
            if (typeof(T) == typeof(Color)) return For<Color, ColorOps>();
            if (typeof(T) == typeof(Thickness)) return For<Thickness, ThicknessOps>();
            if (typeof(T) == typeof(Rect)) return For<Rect, RectOps>();
            if (typeof(T) == typeof(Matrix3x2)) return For<Matrix3x2, Matrix3x2Ops>();
            return default;
        }

        private static DefaultFunctions For<TValue, TOps>() where TValue : unmanaged where TOps : struct, IValueOps<TValue> =>
            new(&DeclareDefaults<TValue, TOps>, &StartDefault<TValue, TOps>);
    }

    private struct KeyframeCopy : IComponent
    {
        internal Keyframe[]? Values;
    }

    private static void DeclareDefaults<T, TOps>(World world, Entity target)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        DeclareStorage<T>(world, target);
        if (!world.HasBuffer<PropertySource<T>>(target)) world.AddBuffer<PropertySource<T>>(target);
        if (!world.HasBuffer<MotionDeclaration<T>>(target)) world.AddBuffer<MotionDeclaration<T>>(target);
        if (!world.HasBuffer<MotionStop>(target)) world.AddBuffer<MotionStop>(target);
        if (!world.HasBuffer<DirectBoundary>(target)) world.AddBuffer<DirectBoundary>(target);
        if (!world.HasBuffer<TimelineStage>(target)) world.AddBuffer<TimelineStage>(target);
        if (!world.Has<KeyframeCopy>(target)) world.Add(target, new KeyframeCopy());
        DeclareDefaultBuffer<T, TOps, TweenParameters, TweenState, Tween<T, TOps, Linear>>(world, target);
        DeclareDefaultBuffer<T, TOps, SpringParameters, SpringState<T>, Spring<T, TOps>>(world, target);
        DeclareDefaultBuffer<T, TOps, KeyframeParameters, KeyframeState, Keyframes<T, TOps>>(world, target);
        DeclareDefaultBuffer<T, TOps, FunctionTweenParameters, TweenState, FunctionTween<T, TOps>>(world, target);
    }

    private static void DeclareDefaultBuffer<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        if (!world.HasBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(target))
            world.AddBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(target);
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(target,
            static instances => instances.EnsureCapacity(4));
    }

    private static unsafe Entity StartDefault<T, TOps>(World world, Entity target, ref byte field, ref byte value,
        Spec spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        Channel<T> channel = Unsafe.As<byte, Channel<T>>(ref field);
        T destination = Unsafe.As<byte, T>(ref value);
        return spec.Curve.Kind switch
        {
            CurveKind.Linear => StartDefaultMotion(world, target, channel, destination, Spec.Tween<T, TOps, Linear>(spec.Duration), layer, endpoints),
            CurveKind.Easing => StartDefaultMotion(world, target, channel, destination,
                Spec.FromFunction<T, TOps>(spec.Duration, Easing.Resolve(spec.Curve.Easing)), layer, endpoints),
            CurveKind.Function => StartDefaultMotion(world, target, channel, destination,
                Spec.FromFunction<T, TOps>(spec.Duration, spec.Curve.Function), layer, endpoints),
            CurveKind.Spring => StartDefaultMotion(world, target, channel, destination,
                Spec.Spring<T, TOps>(spec.Curve.Stiffness, spec.Curve.Damping, spec.Curve.Tolerance), layer, endpoints),
            CurveKind.Keyframes => StartDefaultMotion(world, target, channel, destination,
                Spec.Keyframes<T, TOps>(spec.Duration, CopyDefaultKeyframes(world, target, channel, layer, endpoints, in spec.Curve)), layer, endpoints),
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
    }

    private static Entity StartDefaultMotion<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target, Channel<T> channel,
        T value, Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        PropertySource<T> source = FindSource(world, target, channel, layer, endpoints);
        Entity animation = source.Animation;
        if (animation != Entity.Null && animation != target && endpoints == TrackEndpoints.Values &&
            world.Has<Timeline>(target) && world.HasBuffer<Track<T>>(target))
            animation = target;
        if (animation == Entity.Null && world.Has<Timeline>(target) && world.HasBuffer<Track<T>>(target)) animation = target;
        if (animation != Entity.Null)
        {
            var declaration = (Channel: channel, Layer: layer, Type: MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type, Capacity: 0);
            if (world.HasBuffer<MotionDeclaration<T>>(animation))
                world.ExecuteBufferRead<MotionDeclaration<T>, (Channel<T> Channel, byte Layer, int Type, int Capacity)>(animation, ref declaration,
                    static (declarations, ref declaration) =>
                    {
                        foreach (MotionDeclaration<T> value in declarations.AsSpan())
                            if (value.Channel == declaration.Channel && value.Layer == declaration.Layer && value.Type == declaration.Type)
                            { declaration.Capacity = value.Capacity; return; }
                    });
            if (declaration.Capacity == 0 || source.Animation == Entity.Null)
            {
                var count = (Animation: animation, Channel: channel, Layer: layer, Count: 0);
                world.ExecuteBufferRead<PropertySource<T>, (Entity Animation, Channel<T> Channel, byte Layer, int Count)>(target, ref count,
                    static (sources, ref count) =>
                    {
                        foreach (PropertySource<T> value in sources.AsSpan())
                            if (value.Animation == count.Animation && value.Channel == count.Channel && value.Layer == count.Layer) count.Count++;
                    });
                int capacity = source.Animation == Entity.Null ? count.Count + 1 : Math.Max(1, count.Count);
                DeclareTyped(world, animation, target, channel, spec, capacity, layer);
            }
        }
        return StartTyped(world, target, channel, value, spec, layer, endpoints);
    }

    private static unsafe void MoveTypedSource<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target, PropertySource<T> source)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        Track<T> track = ReadTrack<T>(world, source.Animation, source.Track);
        int destination = -1;
        int count = BufferCount<PropertySource<T>>(world, target);
        for (int index = 0; index < count; index++)
        {
            PropertySource<T> candidate = ReadSource<T>(world, target, index);
            if (candidate.Animation != target || candidate.Channel != source.Channel || candidate.Layer != source.Layer) continue;
            Track<T> available = ReadTrack<T>(world, target, candidate.Track);
            if (available.FromGraph || available.SourceActive && (!available.Finished || HoldsLayerValue(world, target, in available))) continue;
            if (available.MotionInitialized) MotionAccesses<T>.Items[available.MotionType - 1].Release(world, target, available.MotionIndex);
            available.SourceActive = false;
            available.MotionInitialized = false;
            WriteTrack(world, target, candidate.Track, in available);
            destination = candidate.Track;
            break;
        }
        DevelopmentCheck.Require(destination >= 0, "Moving a source requires declared target capacity.");
        Timeline previous = world.Read<Timeline>(source.Animation);
        Timeline timeline = world.Read<Timeline>(target);
        if (!world.IsEnabled<Timeline>(target))
        {
            timeline.Time = previous.Time;
            timeline.Stage = previous.Stage;
            timeline.Starts++;
            timeline.Paused = previous.Paused;
            world.Replace(target, timeline);
            if (world.IsEnabled<Timeline>(source.Animation)) world.Enable<Timeline>(target);
        }
        int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        bool same = track.MotionType == type;
        int state = same ? FreeMotionIndex<T, TOps, TParameters, TState, TEvaluator>(world, target) : track.MotionIndex;
        if (same)
        {
            MotionInstance<T, TOps, TParameters, TState, TEvaluator> value = ReadBufferItem<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(world, source.Animation, track.MotionIndex);
            var transfer = (Index: state, Value: value);
            world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, (int Index, MotionInstance<T, TOps, TParameters, TState, TEvaluator> Value)>(target, ref transfer,
                static (instances, ref transfer) => instances[transfer.Index] = transfer.Value);
            int oldIndex = track.MotionIndex;
            world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, int>(source.Animation, ref oldIndex,
                static (instances, ref oldIndex) => instances[oldIndex] = default);
        }
        else if (track.MotionInitialized)
            MotionAccesses<T>.Items[track.MotionType - 1].Release(world, source.Animation, track.MotionIndex);
        Track<T> moved = track;
        moved.Target = Entity.Null;
        moved.MotionIndex = state;
        moved.MotionInitialized &= same;
        moved.Start += timeline.Time - previous.Time;
        moved.SampleTime += timeline.Time - previous.Time;
        moved.StartId = timeline.Starts;
        moved.Stage = timeline.Stage;
        WriteTrack(world, target, destination, in moved);
        UpdateDirectBoundary(world, target, destination, in moved, in timeline, new(moved.End, moved.Duration));
        var relation = (Previous: source.Animation, Track: source.Track, Target: target, Next: destination, Source: source);
        world.ExecuteBufferWrite<PropertySource<T>, (Entity Previous, int Track, Entity Target, int Next, PropertySource<T> Source)>(target, ref relation,
            static (sources, ref relation) =>
            {
                for (int index = 0; index < sources.Count; index++)
                {
                    PropertySource<T> value = sources.Read(index);
                    if (value.Animation != relation.Target || value.Track != relation.Next) continue;
                    PropertySource<T> moved = relation.Source;
                    moved.Animation = relation.Target;
                    moved.Track = relation.Next;
                    sources[index] = moved;
                }
                for (int index = sources.Count - 1; index >= 0; index--)
                    if (sources.Read(index).Animation == relation.Previous && sources.Read(index).Track == relation.Track)
                        sources.SwapRemoveAt(index);
            });
        track.SourceActive = false;
        track.MotionInitialized = false;
        track.Finished = true;
        WriteTrack(world, source.Animation, source.Track, in track);
        RemoveDirectBoundary<T>(world, source.Animation, source.Track, in previous);
        bool running = false;
        world.ExecuteBufferRead<Track<T>, bool>(source.Animation, ref running, static (tracks, ref running) =>
        {
            foreach (Track<T> value in tracks.AsSpan()) running |= value.SourceActive && !value.Finished;
        });
        if (!running) world.Disable<Timeline>(source.Animation);
        RefreshSources<T>(world, target);
    }

    private static ReadOnlyMemory<Keyframe> CopyKeyframes(World world, Entity target, in Curve curve)
    {
        if (!world.Has<KeyframeCopy>(target)) world.Add(target, new KeyframeCopy());
        KeyframeCopy copy = world.Read<KeyframeCopy>(target);
        var range = (Start: curve.KeyframeStart, Count: curve.KeyframeCount, Values: copy.Values);
        world.ExecuteBufferRead<Keyframe, (int Start, int Count, Keyframe[]? Values)>(target, ref range,
            static (keys, ref range) =>
            {
                ReadOnlySpan<Keyframe> selected = keys.AsSpan().Slice(range.Start, range.Count == 0 ? keys.Count - range.Start : range.Count);
                if (range.Values is null || !selected.SequenceEqual(range.Values)) range.Values = selected.ToArray();
            });
        if (!ReferenceEquals(copy.Values, range.Values))
        {
            copy.Values = range.Values;
            world.Replace(target, copy);
        }
        return range.Values;
    }

    private static ReadOnlyMemory<Keyframe> CopyDefaultKeyframes<T>(World world, Entity target, Channel<T> channel,
        byte layer, TrackEndpoints endpoints, in Curve curve) where T : unmanaged
    {
        PropertySource<T> source = FindSource(world, target, channel, layer, endpoints);
        Entity keys = source.Animation != Entity.Null && world.HasBuffer<Keyframe>(source.Animation) ? source.Animation : target;
        return CopyKeyframes(world, keys, in curve);
    }
}

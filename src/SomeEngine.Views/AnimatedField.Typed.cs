using System.Runtime.CompilerServices;
using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal abstract class FieldMotion
{
    internal FieldMotion? Next;
}

internal sealed class FieldMotion<T, TOps, TParameters, TState, TEvaluator> : FieldMotion
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    internal Channel<T> Channel;
    internal Spec<T, TOps, TParameters, TState, TEvaluator> Spec;

    internal static FieldMotion<T, TOps, TParameters, TState, TEvaluator> Select(ref AnimatedField field)
    {
        if (field.Motion is FieldMotion<T, TOps, TParameters, TState, TEvaluator> current)
            return current;
        for (FieldMotion? declared = field.Motions; declared is not null; declared = declared.Next)
            if (declared is FieldMotion<T, TOps, TParameters, TState, TEvaluator> existing)
                return existing;
        var motion = new FieldMotion<T, TOps, TParameters, TState, TEvaluator> { Next = field.Motions };
        field.Motions = motion;
        return motion;
    }
}

internal static unsafe partial class AnimatedFields
{
    internal static void Declare<T, TOps, TParameters, TState, TEvaluator>(World world, Entity entity,
        Channel<T> channel, Spec<T, TOps, TParameters, TState, TEvaluator> spec, T value, bool hasValue)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        PropertyAnimation.Declare(world, entity, channel, spec);
        Declare(world, entity, channel, default(Spec), value, hasValue, keepMotion: true);
        using DynamicBuffer<AnimatedField> fields = world.GetBuffer<AnimatedField>(entity);
        for (int index = 0; index < fields.Count; index++)
        {
            ref AnimatedField field = ref fields[index];
            if (field.Component != channel.Component || field.Field != channel.Field) continue;
            var motion = FieldMotion<T, TOps, TParameters, TState, TEvaluator>.Select(ref field);
            field.Motion = motion;
            motion.Channel = channel;
            motion.Spec = spec;
            field.ValueFunction = &CopyTypedValue<T, TOps, TParameters, TState, TEvaluator>;
            field.SubmitFunction = &SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>;
            return;
        }
    }

    internal static T ReadTarget<T>(World world, Entity entity, Channel<T> channel) where T : unmanaged
    {
        if (world.Has<Buffer<BaseValue<T>>>(entity))
        {
            using ReadOnlyBuffer<BaseValue<T>> values = world.ReadBuffer<BaseValue<T>>(entity);
            foreach (BaseValue<T> value in values.AsSpan())
                if (value.Channel == channel) return value.Value;
        }
        return channel.Read(world, entity);
    }

    internal static void Submit<T>(World world, Entity entity, Channel<T> channel, in T value) where T : unmanaged
    {
        var target = (Channel: channel, Index: -1, Field: default(AnimatedField));
        if (world.Has<Buffer<AnimatedField>>(entity))
        {
            using ReadOnlyBuffer<AnimatedField> fields = world.ReadBuffer<AnimatedField>(entity);
            for (int index = 0; index < fields.Count; index++)
            {
                AnimatedField field = fields[index];
                if (field.Component != channel.Component || field.Field != channel.Field) continue;
                target.Field = field;
                target.Index = index;
                break;
            }
        }
        if (target.Index >= 0)
        {
            T submitted = value;
            bool hadValue = target.Field.HasValue;
            target.Field.SubmitFunction(world, entity, ref target.Field, ref Unsafe.As<T, byte>(ref submitted));
            if (target.Field.HasValue != hadValue)
                {
                    using var fields = world.GetBuffer<AnimatedField>(entity);
                    fields[target.Index] = target.Field;
                }
        }
        else channel.Write(world, entity, in value);
    }

    private static Channel<T> FindChannel<T>(World world, Entity entity, in AnimatedField field) where T : unmanaged
    {
        using ReadOnlyBuffer<BaseValue<T>> values = world.ReadBuffer<BaseValue<T>>(entity);
        foreach (BaseValue<T> value in values.AsSpan())
            if (value.Channel.Component == field.Component && value.Channel.Field == field.Field) return value.Channel;
        return default;
    }

    private static void SubmitValue<T>(World world, Entity entity, ref AnimatedField field, ref byte bytes) where T : unmanaged
    {
        Channel<T> channel = FindChannel<T>(world, entity, in field);
        T target = Unsafe.ReadUnaligned<T>(ref bytes);
        T previous = ReadTarget(world, entity, channel);
        if (!SameValue(in target, in previous))
            PropertyAnimation.SetBase(world, entity, channel, target);
        PropertyAnimation.To(world, entity, channel, target, field.Spec);
        field.HasValue = true;
    }

    private static void CopyTypedValue<T, TOps, TParameters, TState, TEvaluator>(World world, Entity entity,
        ref AnimatedField field, ref byte component, in byte presentation, FieldValue kind)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var motion = (FieldMotion<T, TOps, TParameters, TState, TEvaluator>)field.Motion!;
        Channel<T> channel = motion.Channel;
        if (kind == FieldValue.Target)
        {
            T target = channel.ReadRoot(in component);
            SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>(world, entity, ref field,
                ref Unsafe.As<T, byte>(ref target), in motion);
        }
        else if (field.HasValue)
        {
            T value = kind == FieldValue.Base ? ReadTarget(world, entity, channel) : channel.ReadRoot(in presentation);
            channel.WriteRoot(ref component, in value);
        }
    }

    private static void SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>(World world, Entity entity,
        ref AnimatedField field, ref byte bytes)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var motion = (FieldMotion<T, TOps, TParameters, TState, TEvaluator>)field.Motion!;
        SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>(world, entity, ref field, ref bytes, in motion);
    }

    private static void SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>(World world, Entity entity,
        ref AnimatedField field, ref byte bytes, in FieldMotion<T, TOps, TParameters, TState, TEvaluator> motion)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        T target = Unsafe.ReadUnaligned<T>(ref bytes);
        T previous = ReadTarget(world, entity, motion.Channel);
        if (!TOps.Equals(in target, in previous))
            PropertyAnimation.SetBase(world, entity, motion.Channel, target);
        if (field.HasValue)
            PropertyAnimation.To(world, entity, motion.Channel, target, motion.Spec);
        else
        {
            field.HasValue = true;
            motion.Channel.Write(world, entity, in target);
        }
    }
}

using System.Runtime.CompilerServices;
using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

[BufferCapacity(1)]
internal struct FieldMotion<T, TOps, TParameters, TState, TEvaluator> : IBufferElement
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    internal Channel<T> Channel;
    internal Spec<T, TOps, TParameters, TState, TEvaluator> Spec;
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
        Declare(world, entity, channel, default(Spec), value, hasValue);
        if (!world.HasBuffer<FieldMotion<T, TOps, TParameters, TState, TEvaluator>>(entity))
            world.AddBuffer<FieldMotion<T, TOps, TParameters, TState, TEvaluator>>(entity);
        var motion = new FieldMotion<T, TOps, TParameters, TState, TEvaluator> { Channel = channel, Spec = spec };
        world.ExecuteBufferWrite<FieldMotion<T, TOps, TParameters, TState, TEvaluator>, FieldMotion<T, TOps, TParameters, TState, TEvaluator>>(
            entity, ref motion, static (motions, ref motion) =>
            {
                for (int index = 0; index < motions.Count; index++)
                    if (motions.Read(index).Channel == motion.Channel)
                    {
                        motions[index] = motion;
                        return;
                    }
                motions.Add(motion);
            });
        var identity = (channel.Component, channel.Field);
        world.ExecuteBufferWrite<AnimatedField, (ushort Component, uint Field)>(entity, ref identity,
            static (fields, ref identity) =>
            {
                for (int index = 0; index < fields.Count; index++)
                {
                    ref AnimatedField field = ref fields[index];
                    if (field.Component != identity.Component || field.Field != identity.Field)
                        continue;
                    field.ValueFunction = &CopyTypedValue<T, TOps, TParameters, TState, TEvaluator>;
                    field.SubmitFunction = &SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>;
                    return;
                }
            });
    }

    internal static T ReadTarget<T>(World world, Entity entity, Channel<T> channel) where T : unmanaged
    {
        if (world.HasBuffer<BaseValue<T>>(entity))
        {
            var basis = (Channel: channel, Value: default(T), Found: false);
            world.ExecuteBufferRead<BaseValue<T>, (Channel<T> Channel, T Value, bool Found)>(entity, ref basis,
                static (values, ref basis) =>
                {
                    foreach (BaseValue<T> value in values.AsSpan())
                        if (value.Channel == basis.Channel)
                        {
                            basis.Value = value.Value;
                            basis.Found = true;
                            return;
                        }
                });
            if (basis.Found) return basis.Value;
        }
        return channel.Read(world, entity);
    }

    internal static void Submit<T>(World world, Entity entity, Channel<T> channel, in T value) where T : unmanaged
    {
        var target = (Channel: channel, Index: -1, Field: default(AnimatedField));
        if (world.HasBuffer<AnimatedField>(entity))
            world.ExecuteBufferRead<AnimatedField, (Channel<T> Channel, int Index, AnimatedField Field)>(
                entity, ref target, static (fields, ref target) =>
                {
                    for (int index = 0; index < fields.Count; index++)
                    {
                        AnimatedField field = fields.Read(index);
                        if (field.Component != target.Channel.Component || field.Field != target.Channel.Field)
                            continue;
                        target.Field = field;
                        target.Index = index;
                        return;
                    }
                });
        if (target.Index >= 0)
        {
            T submitted = value;
            target.Field.SubmitFunction(world, entity, ref target.Field, ref Unsafe.As<T, byte>(ref submitted));
            world.ExecuteBufferWrite<AnimatedField, (Channel<T> Channel, int Index, AnimatedField Field)>(
                entity, ref target, static (fields, ref target) => fields[target.Index] = target.Field);
        }
        else channel.Write(world, entity, in value);
    }

    private static Channel<T> FindChannel<T>(World world, Entity entity, in AnimatedField field) where T : unmanaged
    {
        var basis = (Component: field.Component, Field: field.Field, Channel: default(Channel<T>));
        world.ExecuteBufferRead<BaseValue<T>, (ushort Component, uint Field, Channel<T> Channel)>(entity, ref basis,
            static (values, ref basis) =>
            {
                foreach (BaseValue<T> value in values.AsSpan())
                    if (value.Channel.Component == basis.Component && value.Channel.Field == basis.Field)
                    {
                        basis.Channel = value.Channel;
                        return;
                    }
            });
        return basis.Channel;
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

    private static FieldMotion<T, TOps, TParameters, TState, TEvaluator> ReadMotion<T, TOps, TParameters, TState, TEvaluator>(
        World world, Entity entity, in AnimatedField field)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var result = (Component: field.Component, Field: field.Field, Motion: default(FieldMotion<T, TOps, TParameters, TState, TEvaluator>));
        world.ExecuteBufferRead<FieldMotion<T, TOps, TParameters, TState, TEvaluator>,
            (ushort Component, uint Field, FieldMotion<T, TOps, TParameters, TState, TEvaluator> Motion)>(entity, ref result,
            static (motions, ref result) =>
            {
                foreach (var motion in motions.AsSpan())
                    if (motion.Channel.Component == result.Component && motion.Channel.Field == result.Field)
                    {
                        result.Motion = motion;
                        return;
                    }
            });
        return result.Motion;
    }

    private static void CopyTypedValue<T, TOps, TParameters, TState, TEvaluator>(World world, Entity entity,
        ref AnimatedField field, ref byte component, FieldValue kind)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var motion = ReadMotion<T, TOps, TParameters, TState, TEvaluator>(world, entity, in field);
        Channel<T> channel = motion.Channel;
        if (kind == FieldValue.Target)
        {
            T target = channel.ReadRoot(in component);
            SubmitTypedValue<T, TOps, TParameters, TState, TEvaluator>(world, entity, ref field,
                ref Unsafe.As<T, byte>(ref target), in motion);
        }
        else if (field.HasValue)
        {
            T value = kind == FieldValue.Base ? ReadTarget(world, entity, channel) : channel.Read(world, entity);
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
        var motion = ReadMotion<T, TOps, TParameters, TState, TEvaluator>(world, entity, in field);
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

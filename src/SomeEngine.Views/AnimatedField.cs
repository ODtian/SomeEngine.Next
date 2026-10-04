using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Animation;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A caller-written field and the typed function used at component-copy commit.</summary>
[BufferCapacity(4)]
internal unsafe struct AnimatedField : IBufferElement
{
    internal ushort Component;
    internal ushort Offset;
    internal Spec Spec;
    internal bool HasValue;
#if DEBUG
    internal int ValueComponent;
#endif
    internal delegate*<World, Entity, ref AnimatedField, ref byte, FieldValue, void> ValueFunction;
}

internal enum FieldValue : byte { Base, Presentation, Target }

internal static unsafe class AnimatedFields
{
    internal static void Declare<T>(World world, Entity entity, Channel<T> channel, Spec spec, T value, bool hasValue)
        where T : unmanaged
    {
        PropertyAnimation.Declare<T>(world, entity);
        Timeline timeline = world.Read<Timeline>(entity);
        if (timeline.Clock != TimelineClock.Interface)
        {
            timeline.Clock = TimelineClock.Interface;
            world.Replace(entity, timeline);
        }
        if (!world.HasBuffer<AnimatedField>(entity))
            world.AddBuffer<AnimatedField>(entity);
        var declaration = (Field: new AnimatedField
        {
            Component = channel.Component, Offset = channel.Offset, Spec = spec,
            HasValue = hasValue, ValueFunction = &CopyValue<T>,
#if DEBUG
            ValueComponent = BufferComponents.Header<BaseValue<T>>(),
#endif
        }, Added: false);
        world.ExecuteBufferWrite<AnimatedField, (AnimatedField Field, bool Added)>(entity, ref declaration,
            static (fields, ref declaration) =>
            {
                for (int index = 0; index < fields.Count; index++)
                {
                    AnimatedField field = fields.Read(index);
                    if (field.Component != declaration.Field.Component || field.Offset != declaration.Field.Offset)
                        continue;
#if DEBUG
                    DevelopmentCheck.Require(field.ValueComponent == declaration.Field.ValueComponent,
                        "An animated field keeps one value type at its component offset.");
#endif
                    field.Spec = declaration.Field.Spec;
                    fields[index] = field;
                    return;
                }
                fields.Add(declaration.Field);
                declaration.Added = true;
            });
        if (declaration.Added)
            PropertyAnimation.SetBase(world, entity, channel, value);
    }

    internal static void RestoreBase<T>(World world, Entity entity, ref T value) where T : struct =>
        Copy(world, entity, ref value, FieldValue.Base);

    internal static bool PreservePresentation<T>(World world, Entity entity, ref T value) where T : struct =>
        Copy(world, entity, ref value, FieldValue.Presentation);

    internal static void SubmitTargets<T>(World world, Entity entity, ref T value) where T : struct =>
        Copy(world, entity, ref value, FieldValue.Target);

    private static bool Copy<T>(World world, Entity entity, ref T value, FieldValue fieldValue) where T : struct
    {
        if (!world.HasBuffer<AnimatedField>(entity))
            return false;
        var data = (World: world, Entity: entity, Value: value, FieldValue: fieldValue, Found: false);
        world.ExecuteBufferWrite<AnimatedField, (World World, Entity Entity, T Value, FieldValue FieldValue, bool Found)>(entity, ref data,
            static (fields, ref data) =>
            {
                for (int index = 0; index < fields.Count; index++)
                {
                    AnimatedField field = fields.Read(index);
                    if (field.Component != ComponentId<T>.Value)
                        continue;
                    data.Found = true;
                    field.ValueFunction(data.World, data.Entity, ref field,
                        ref Unsafe.As<T, byte>(ref data.Value), data.FieldValue);
                    if (data.FieldValue == FieldValue.Target) fields[index] = field;
                }
            });
        value = data.Value;
        return data.Found;
    }

    private static void CopyValue<T>(World world, Entity entity, ref AnimatedField field, ref byte component, FieldValue fieldValue)
        where T : unmanaged
    {
        ref byte bytes = ref Unsafe.Add(ref component, field.Offset);
        if (fieldValue == FieldValue.Presentation)
        {
            if (field.HasValue)
            {
                T presented = Unsafe.ReadUnaligned<T>(in world.ReadBytes(entity, field.Component, field.Offset, Unsafe.SizeOf<T>()));
                Unsafe.WriteUnaligned(ref bytes, presented);
            }
            return;
        }
        var basis = (Component: field.Component, Offset: field.Offset, Value: default(BaseValue<T>));
        world.ExecuteBufferRead<BaseValue<T>, (ushort Component, ushort Offset, BaseValue<T> Value)>(entity, ref basis,
            static (values, ref basis) =>
            {
                foreach (BaseValue<T> value in values.AsSpan())
                    if (value.Channel.Component == basis.Component && value.Channel.Offset == basis.Offset)
                    {
                        basis.Value = value;
                        return;
                    }
            });
        if (fieldValue == FieldValue.Base)
        {
            if (field.HasValue) Unsafe.WriteUnaligned(ref bytes, basis.Value.Value);
            return;
        }
        T target = Unsafe.ReadUnaligned<T>(ref bytes);
        if (!field.HasValue)
        {
            field.HasValue = true;
            PropertyAnimation.SetBase(world, entity, basis.Value.Channel, target);
            return;
        }
        if (SameValue(in target, in basis.Value.Value))
            return;
        PropertyAnimation.SetBase(world, entity, basis.Value.Channel, target);
        PropertyAnimation.To(world, entity, basis.Value.Channel, target, field.Spec);
    }

    // Field bytes are also the public property-animation address and support every unmanaged value type.
    private static bool SameValue<T>(in T left, in T right) where T : unmanaged =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)), Unsafe.SizeOf<T>())
            .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in right)), Unsafe.SizeOf<T>()));
}

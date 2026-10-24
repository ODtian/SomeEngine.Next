using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.Animation;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A caller-written field and the typed function used at component-copy commit.</summary>
[BufferCapacity(AnimatedFields.InlineCapacity)]
internal unsafe struct AnimatedField : IBufferElement
{
    internal ushort Component;
    internal ushort Offset;
    internal uint Field;
    internal bool ThroughReference;
    internal Spec Spec;
    internal FieldMotion? Motions;
    internal FieldMotion? Motion;
    internal bool HasValue;
#if DEBUG
    internal int ValueComponent;
#endif
    internal delegate*<World, Entity, ref AnimatedField, ref byte, in byte, FieldValue, void> ValueFunction;
    internal delegate* managed<World, Entity, ref AnimatedField, ref byte, void> SubmitFunction;
}

internal enum FieldValue : byte { Base, Presentation, Target }

internal static unsafe partial class AnimatedFields
{
    internal const int InlineCapacity = 4;

    [InlineArray(InlineCapacity)]
    private struct FieldCopies
    {
        private AnimatedField _first;
    }

    internal static void Declare<T>(World world, Entity entity, Channel<T> channel, Spec spec, T value, bool hasValue,
        bool keepMotion = false)
        where T : unmanaged
    {
        if (!keepMotion || !world.HasBuffer<Track<T>>(entity))
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
            Component = channel.Component, Offset = channel.Offset, Field = channel.Field, Spec = spec,
            ThroughReference = channel.ThroughReference,
            HasValue = hasValue, ValueFunction = &CopyValue<T>, SubmitFunction = &SubmitValue<T>,
#if DEBUG
            ValueComponent = BufferComponents.Header<BaseValue<T>>(),
#endif
        }, Added: false, KeepMotion: keepMotion);
        world.ExecuteBufferWrite<AnimatedField, (AnimatedField Field, bool Added, bool KeepMotion)>(entity, ref declaration,
            static (fields, ref declaration) =>
            {
                for (int index = 0; index < fields.Count; index++)
                {
                    AnimatedField field = fields.Read(index);
                    if (field.Component != declaration.Field.Component || field.Field != declaration.Field.Field)
                        continue;
#if DEBUG
                    DevelopmentCheck.Require(field.ValueComponent == declaration.Field.ValueComponent,
                        "An animated field keeps one value type at its component offset.");
#endif
                    field.Spec = declaration.Field.Spec;
                    if (!declaration.KeepMotion) field.Motion = null;
                    field.ValueFunction = declaration.Field.ValueFunction;
                    field.SubmitFunction = declaration.Field.SubmitFunction;
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
        Copy(world, entity, ref value, in value, FieldValue.Base);

    internal static bool PreservePresentation<T>(World world, Entity entity, ref T value, in T presented) where T : struct =>
        Copy(world, entity, ref value, in presented, FieldValue.Presentation);

    internal static void SubmitTargets<T>(World world, Entity entity, ref T value) where T : struct =>
        Copy(world, entity, ref value, in value, FieldValue.Target);

    private static bool Copy<T>(World world, Entity entity, ref T value, in T presented, FieldValue fieldValue) where T : struct
    {
        if (!world.HasBuffer<AnimatedField>(entity))
            return false;
        var selection = (Component: ComponentId<T>.Value, Count: 0, Found: false, Fields: default(FieldCopies));
        world.ExecuteBufferRead<AnimatedField, (ushort Component, int Count, bool Found, FieldCopies Fields)>(entity, ref selection,
            static (fields, ref selection) =>
            {
                selection.Count = fields.Count;
                foreach (ref readonly AnimatedField field in fields.AsSpan())
                    if (field.Component == selection.Component && !field.ThroughReference)
                    {
                        selection.Found = true;
                        break;
                    }
                if (selection.Found && selection.Count <= InlineCapacity)
                    fields.AsSpan().CopyTo(selection.Fields);
            });
        if (!selection.Found) return false;
        int count = selection.Count;
        AnimatedField[]? overflow = null;
        Span<AnimatedField> fields = selection.Fields;
        if (count > InlineCapacity)
        {
            overflow = ArrayPool<AnimatedField>.Shared.Rent(count);
            var storage = (Fields: overflow, Count: count);
            world.ExecuteBufferRead<AnimatedField, (AnimatedField[] Fields, int Count)>(entity, ref storage,
                static (fields, ref storage) => fields.AsSpan().CopyTo(storage.Fields.AsSpan(0, storage.Count)));
            fields = overflow.AsSpan(0, count);
        }
        else fields = fields[..count];
        try
        {
            bool changed = false;
            for (int index = 0; index < count; index++)
            {
                ref AnimatedField field = ref fields[index];
                if (field.Component != ComponentId<T>.Value || field.ThroughReference)
                    continue;
                bool hadValue = field.HasValue;
                field.ValueFunction(world, entity, ref field, ref Unsafe.As<T, byte>(ref value),
                    in Unsafe.As<T, byte>(ref Unsafe.AsRef(in presented)), fieldValue);
                changed |= field.HasValue != hadValue;
            }
            if (changed)
            {
                if (overflow is null)
                    world.ExecuteBufferWrite<AnimatedField, (ushort Component, int Count, bool Found, FieldCopies Fields)>(
                        entity, ref selection, static (fields, ref selection) =>
                            ((ReadOnlySpan<AnimatedField>)selection.Fields)[..selection.Count].CopyTo(fields.AsSpan()));
                else
                {
                    var storage = (Fields: overflow, Count: count);
                    world.ExecuteBufferWrite<AnimatedField, (AnimatedField[] Fields, int Count)>(entity, ref storage,
                        static (fields, ref storage) => storage.Fields.AsSpan(0, storage.Count).CopyTo(fields.AsSpan()));
                }
            }
            return true;
        }
        finally
        {
            if (overflow is not null) ArrayPool<AnimatedField>.Shared.Return(overflow, clearArray: true);
        }
    }

    private static void CopyValue<T>(World world, Entity entity, ref AnimatedField field, ref byte component,
        in byte presentation, FieldValue fieldValue)
        where T : unmanaged
    {
        Channel<T> channel = FindChannel<T>(world, entity, in field);
        if (fieldValue == FieldValue.Presentation)
        {
            if (field.HasValue)
            {
                T presented = channel.ReadRoot(in presentation);
                channel.WriteRoot(ref component, in presented);
            }
            return;
        }
        var basis = (Component: field.Component, Field: field.Field, Value: default(BaseValue<T>));
        world.ExecuteBufferRead<BaseValue<T>, (ushort Component, uint Field, BaseValue<T> Value)>(entity, ref basis,
            static (values, ref basis) =>
            {
                foreach (BaseValue<T> value in values.AsSpan())
                    if (value.Channel.Component == basis.Component && value.Channel.Field == basis.Field)
                    {
                        basis.Value = value;
                        return;
                    }
            });
        if (fieldValue == FieldValue.Base)
        {
            if (field.HasValue) channel.WriteRoot(ref component, in basis.Value.Value);
            return;
        }
        T target = channel.ReadRoot(in component);
        if (!field.HasValue)
        {
            field.HasValue = true;
            PropertyAnimation.SetBase(world, entity, basis.Value.Channel, target);
            return;
        }
        if (!SameValue(in target, in basis.Value.Value))
            PropertyAnimation.SetBase(world, entity, basis.Value.Channel, target);
        PropertyAnimation.To(world, entity, basis.Value.Channel, target, field.Spec);
    }

    private static bool SameValue<T>(in T left, in T right) where T : unmanaged =>
        EqualityComparer<T>.Default.Equals(left, right);
}

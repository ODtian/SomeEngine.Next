using SomeEngine.ECS.Components;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

/// <summary>The format and value codec selected for one component storage type.</summary>
public sealed partial class ComponentCodec
{
    public ComponentType Component { get; }
    public SerializationTypeKey Format => Value.Key;
    internal SavedType Value { get; }
    internal SavedType[] Related { get; }
    internal SavedService[] Services { get; }

    private ComponentCodec(ComponentType component, SavedType value, SavedType[] related, SavedService[]? services = null)
        => (Component, Value, Related, Services) = (component, value, related, services ?? []);

    public static ComponentCodec Create<T, TCodec>(TCodec codec)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        ComponentType component = StorageType<T>();
        ComponentCodec description = DefaultDescription(component)!;
        return Create<T, TCodec>(description.Format, codec);
    }

    public static ComponentCodec Create<T, TCodec>(SerializationTypeKey format, TCodec codec)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        ComponentType component = StorageType<T>();
        object? patcher = DefaultDescription(component)?.Value.EntityPatcher;
        if (IsBuffer<T>())
            return Column<Buffer<T>, BufferCodec<T, TCodec>>(format, new(codec), SavedStorage.Buffer,
                (IReferencePatcher<Buffer<T>>?)patcher);
        return ValueCodec<T, TCodec>(format, codec, (IReferencePatcher<T>?)patcher);
    }

    /// <summary>Records a concrete generated default beside its existing component description.</summary>
    public static void RegisterDefault<T, TCodec>(SerializationTypeKey format, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        ComponentCodec description = Create<T, TCodec>(format, codec);
        description.Component.SerializationDescription = description;
    }

    public static void RegisterDefaultTag<T>(SerializationTypeKey format) where T : struct
    {
        var description = new ComponentCodec(ComponentType<T>.Value,
            new SavedColumn<T, RawCodec<T>>(format, SavedStorage.Tag, default, true),
            RelatedValues<T, RawCodec<T>>(format, default, null));
        description.Component.SerializationDescription = description;
    }

    public static void RegisterDefault<T, TCodec, TPatcher>(SerializationTypeKey format,
        TCodec codec = default, TPatcher patcher = default)
        where T : struct where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T>
    {
        IReferencePatcher<T> references = ReferencePatchers.Bind<T, TPatcher>(patcher);
        ComponentCodec description = IsBuffer<T>()
            ? Column<Buffer<T>, BufferCodec<T, TCodec>>(format, new(codec), SavedStorage.Buffer,
                new BufferReferencePatcher<T>(references))
            : ValueCodec<T, TCodec>(format, codec, references);
        description.Component.SerializationDescription = description;
    }

    private static bool IsBuffer<T>() where T : struct => typeof(IBufferElement).IsAssignableFrom(typeof(T));
    private static ComponentType StorageType<T>() where T : struct
        => IsBuffer<T>() ? ComponentType<Buffer<T>>.Value : ComponentType<T>.Value;

    private static ComponentCodec? DefaultDescription(ComponentType component)
        => component.SerializationDescription is ITopologySerializationType topology
            ? Describe(topology) : component.SerializationDescription as ComponentCodec;

    private static ComponentCodec ValueCodec<T, TCodec>(SerializationTypeKey format, TCodec codec,
        IReferencePatcher<T>? patcher) where T : struct where TCodec : struct, IComponentCodec<T>
    {
        ComponentType component = ComponentValues.Bind<T>();
        ComponentCodec? description = DefaultDescription(component);
        SavedStorage storage = description?.Value.Storage ?? (component.IsTag ? SavedStorage.Tag : SavedStorage.Table);
        SavedType value = component.IsSparse
            ? new SavedSparse<T, TCodec>(format, codec, false, patcher)
            : component.IsShared
                ? new SavedShared<T, TCodec>(format, codec, false, patcher)
                : new SavedColumn<T, TCodec>(format, storage, codec, false, patcher);
        SavedType[] related = RelatedValues<T, TCodec>(format, codec, patcher);
        if (description is not null && description.Services.Length != 0)
            related = description.Related.Where(type => !related.Any(replacement => replacement.Type == type.Type && replacement.Storage == type.Storage))
                .Concat(related).ToArray();
        return new(component, value, related, description?.Services);
    }

    private static ComponentCodec Column<T, TCodec>(SerializationTypeKey format, TCodec codec,
        SavedStorage storage, IReferencePatcher<T>? patcher)
        where T : struct where TCodec : struct, IComponentCodec<T>
        => new(ComponentValues.Bind<T>(), new SavedColumn<T, TCodec>(format, storage, codec, false, patcher),
            RelatedValues<T, TCodec>(format, codec, patcher));

    private static SavedType[] RelatedValues<T, TCodec>(SerializationTypeKey format, TCodec codec,
        IReferencePatcher<T>? patcher) where T : struct where TCodec : struct, IComponentCodec<T>
    {
        var related = new List<SavedType>();
        if (ComponentType<T>.IsEnableableType)
            related.Add(new SavedColumn<World.EnableGroup<T>, RawCodec<World.EnableGroup<T>>>(
                SerializationRegistry.Derived(format, 8, "EnableGroup"), SavedStorage.Tag, default, true));
        if (ComponentType<T>.KeepsRemovedType)
            related.Add(new SavedColumn<Removed<T>, RemovedCodec<T, TCodec>>(
                SerializationRegistry.Derived(format, 32, "Removed"), SavedStorage.Removed, new(codec), false,
                ReferencePatchers.Removed(patcher)));
        return related.ToArray();
    }
}

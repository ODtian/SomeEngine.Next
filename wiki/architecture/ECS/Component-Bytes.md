# Component Bytes

## Definition

Unmanaged component data can be read and written by component number. The component's existing table row or sparse row holds the bytes and the write version. Property animation captures its starting value through the read entry point and writes sampled channel values through the write entry point.

```csharp
public static class ComponentId<T> where T : struct
{
    public static int Value { get; }
}

public sealed partial class World
{
    public ref readonly byte ReadBytes(Entity entity, int component);
    public ref readonly byte ReadBytes(Entity entity, int component, int offset, int length);
    public ref byte WriteBytes(Entity entity, int component);
    public ref byte WriteBytes(Entity entity, int component, int offset, int length);
}

public readonly partial struct QueryChunk
{
    public Span<T> Write<T>() where T : struct;
}
```

## Invariants

- `ComponentId<T>.Value` uses the ECS component type number. Its value remains constant across Worlds throughout one process. Values are assigned at first type use and use the native `int` component identifier. Persistent data identifies component types through the serialization APIs.
- `World.WriteBytes` supports table and sparse components. Taking the reference advances that entity's component write version, including writes through the region overload. The whole-component overload returns its first byte; the region overload returns the byte at `offset`.
- `World.ReadBytes` supports the same table and sparse data and returns a read-only reference. Reads preserve the entity and component type write versions and select read access at the existing World access boundary. The whole-component and region overloads return the first component byte and the byte at `offset`, respectively.
- Both region overloads accept nonnegative offsets and lengths whose sum fits within the component's size. `DevelopmentCheck` reports invalid ranges in development builds, including overflowing input values. Callers use a region's declared length when reading or writing through its reference.
- References are used before a structural change, sparse array growth, or another operation replaces the backing storage. The existing World access boundary selects table or sparse read/write access for the requested operation before returning the reference.
- `QueryChunk.Write<T>()` returns the typed column for the current matching range inside `Query.Open()`. For an unmanaged component, `MemoryMarshal.AsBytes(chunk.Write<T>())` exposes that range as bytes. Taking the write span records the range write version; `Changed<T>` observes the same writes.
- Component byte access requires unmanaged data. Dynamic buffer storage uses the buffer APIs, and direct entity byte writes respect public component value-write permissions. Protected topology, shared and indexed values use their typed mutation entries; ordinary byte writes address directly mutable values.
- API usage checks call `SomeEngine.Core.Diagnostics.DevelopmentCheck`; their calls and argument evaluation disappear in Release callers. Component storage and write-version data remain in the existing ECS stores.

## Field writes

Managed component roots and generated field paths use [[architecture/ECS/Component-Fields|Typed Component Fields]]. Their field access and byte access share ECS component numbers, storage permissions and write versions.

A channel holds a component number and a byte offset calculated when the channel is created. Its update can write a typed value through the checked region entry:

```csharp
ref byte field = ref world.WriteBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>());
Unsafe.WriteUnaligned(ref field, value);
```

Starting and retargeting capture a typed field value through the corresponding read-only region:

```csharp
T current = Unsafe.ReadUnaligned<T>(in world.ReadBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>()));
```

Closed generic buffer element types such as `Track<float>` and `Track<Vector2>` use the normal buffer APIs. Their storage and capacity rules are described in [[architecture/ECS/Dynamic-Buffers|Dynamic Buffers]].

## Relations

[[architecture/ECS/Change-Tracking|Change Tracking]] · [[architecture/animation/Property-Animation|Property Animation]] · [[architecture/validation/Development-Checks|Development Checks]]

Source: [World field and byte access](../../../src/SomeEngine.ECS/World.Fields.cs), [native chunk columns](../../../src/SomeEngine.ECS/Queries/Query.cs).

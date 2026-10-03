# Component Bytes

## Definition

Unmanaged component data can be written by component number. The component's existing table row or sparse row holds the bytes and the write version. Property animation uses this entry point to write a channel's field.

```csharp
public static class ComponentId<T> where T : struct
{
    public static ushort Value { get; }
}

public partial class World
{
    public ref byte WriteBytes(Entity entity, ushort component);
    public ref byte WriteBytes(Entity entity, ushort component, int offset, int length);
}

public readonly ref partial struct QueryChunkView
{
    public Span<byte> WriteColumn(ushort component, out int stride);
}
```

## Invariants

- `ComponentId<T>.Value` uses the ECS component type number. Its value remains constant across Worlds throughout one process. Values are assigned at first type use, occupy the range `1..65535` for the byte API, and are checked when first converted to `ushort`. Persistent data identifies component types through the serialization APIs.
- `World.WriteBytes` supports table and sparse components. Taking the reference advances that entity's component write version, including writes through the region overload. The whole-component overload returns its first byte; the region overload returns the byte at `offset`.
- The region overload accepts nonnegative offsets and lengths whose sum fits within the component's size. `DevelopmentCheck` reports invalid ranges in development builds, including overflowing input values. Callers use a region's declared length when writing through its reference.
- References are used before a structural change, sparse array growth, or another operation replaces the backing storage. The existing World access boundary selects table or sparse write access before returning the reference.
- `QueryChunkView.WriteColumn` requires a query that declares write access to the requested table component and selects whole chunks. The returned span covers `Count * stride` bytes, and `stride` is the component size. Obtaining it publishes the query's `CurrentSystemVersion` for every row in the column; `Changed<T>` observes these writes within its usual version window.
- Component byte access requires unmanaged data. Dynamic buffer storage uses the buffer APIs, and direct entity byte access respects public component value-write permissions. Column writes retain the query's relationship write boundary and its hierarchy, relation and index maintenance.
- API usage checks call `SomeEngine.Core.Diagnostics.DevelopmentCheck`; their calls and argument evaluation disappear in Release callers. Component storage and write-version data remain in the existing ECS stores.

## Field writes

A channel holds a component number and a byte offset calculated when the channel is created. Its update can write a typed value through the checked region entry:

```csharp
ref byte field = ref world.WriteBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>());
Unsafe.WriteUnaligned(ref field, value);
```

Closed generic buffer element types such as `Track<float>` and `Track<Vector2>` use the normal buffer APIs. Their storage and capacity rules are described in [[architecture/ECS/Dynamic-Buffers|Dynamic Buffers]].

## Relations

[[architecture/ECS/Change-Tracking|Change Tracking]] · [[architecture/animation/Property-Animation|Property Animation]] · [[architecture/validation/Development-Checks|Development Checks]]

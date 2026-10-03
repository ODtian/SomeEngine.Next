# Dynamic Buffers

## Definition

A struct implementing `SomeEngine.ECS.Components.IBufferElement` is stored in a per-entity `DynamicBuffer<T>`. A closed generic element type such as `Track<float>` or `Track<Vector2>` uses the same public buffer APIs as a nongeneric type. Each closed type has its own buffer storage.

```csharp
[BufferCapacity(2)]
public struct Track<T> : IBufferElement where T : unmanaged
{
    public T Value;
}

world.AddBuffer<Track<float>>(entity);
world.ExecuteBufferWrite<Track<float>>(entity, static buffer =>
{
    buffer.Add(new Track<float> { Value = 1 });
    buffer[0].Value = 2;
});
world.ExecuteBufferRead<Track<float>>(entity, static buffer => Use(buffer.Read(0).Value));
world.RemoveBuffer<Track<float>>(entity);
```

## Invariants

- `World.AddBuffer<T>` registers a closed element type's header and inline storage and attaches both to the entity. `World.HasBuffer<T>` and `World.RemoveBuffer<T>` operate on the same pair.
- `BufferCapacity` accepts capacities from zero through eight; the default is eight. Adding values beyond the selected inline capacity uses overflow storage. Generic type definitions can carry the attribute for all their closed forms.
- `World.ExecuteBufferRead/Write<T>` holds the existing World access and structural protection for the duration of the callback. Query callers use `ReadBuffer<T>` or `Buffer<T>` with corresponding declared buffer access.
- Reads expose values or a read-only span. Writes expose `DynamicBuffer<T>`, including element references and a writable span; callers use those references within the callback and before another buffer operation changes its storage.
- `BufferComponents.Header<T>()` and `Inline<T>()` expose the storage type numbers for query and resource descriptions. Element values are added and edited through the buffer APIs; standalone component byte writes operate on ordinary component data.
- The ECS public behavior tests exercise `Track<float>` and `Track<Vector2>` together on one entity, including growth, element writes, reads and removal.

## Relations

[[architecture/ECS/Component-Bytes|Component Bytes]] · [[architecture/animation/Property-Animation|Property Animation]] · [[Workload-Rules]]


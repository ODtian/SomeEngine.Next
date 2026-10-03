using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal readonly partial struct Components
{
    internal unsafe ref byte WriteBytes(Entity entity, int component, in ComponentInfo info)
    {
        EntityRecord record = _entities.ReadRow(entity);
        int column = record.Archetype!.Column(component);
        var chunk = record.Chunk!;
        uint version = _world.AcquireAdmittedSystemVersion();
        MarkWrite(chunk, column, record.RowInChunk, version);
        _hierarchy.TrackParent(entity, component);
        _relationGraph.TrackEndpointRange(_world, [entity], component);
        _indices.Dirty(component);
        return ref chunk.ComponentRowReference(column, record.RowInChunk, in info.Operations);
    }
}

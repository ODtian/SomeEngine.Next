using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGrid
{
    public DeviceCollisionCandidates<TComponent, TReader> Candidates<TComponent, TReader>(
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(0), DevicePairing.Within),
            reader, rowsPerPacket, rules, transfer, readback);

    public DeviceCollisionCandidates<TComponent, TReader> Candidates<TComponent, TReader>(
        RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Between),
            reader, rowsPerPacket, rules, transfer, readback);

    public DeviceCollisionCandidates<TComponent, TReader> Candidates<TComponent, TReader>(
        RelationQuery<EntityProduct2, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Product),
            reader, rowsPerPacket, rules, transfer, readback);

    public WriteDeviceCollisionInputs<TComponent, TReader> WriteInputs<TComponent, TReader>(
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(0), DevicePairing.Within),
            reader, rowsPerPacket);

    public WriteDeviceCollisionInputs<TComponent, TReader> WriteInputs<TComponent, TReader>(
        RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Between),
            reader, rowsPerPacket);

    public WriteDeviceCollisionInputs<TComponent, TReader> WriteInputs<TComponent, TReader>(
        RelationQuery<EntityProduct2, (Entity, Entity)> pairs, TReader reader, int rowsPerPacket)
        where TComponent : unmanaged, IComponent
        where TReader : struct, IDeviceCollisionReader<TComponent> => new(this,
            new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Product),
            reader, rowsPerPacket);
}

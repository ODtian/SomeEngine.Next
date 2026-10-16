using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static unsafe class TypedFieldSmoke
{
    internal static void Check()
    {
        using var world = new World();
        var value = new ReferenceValue();
        Entity entity = world.CreateEntity(new ReferenceComponent<ReferenceValue> { Value = value });
        uint before = world.WriteVersion<ReferenceComponent<ReferenceValue>>(entity);
        world.WriteField<ReferenceComponent<ReferenceValue>, float, ReferenceAccess>(entity, 0.5f);
        Require(world.ReadField<ReferenceComponent<ReferenceValue>, float, ReferenceAccess>(entity) == 0.5f,
            "Typed component fields must support reference-bearing generic roots.");
        Require(world.WriteVersion<ReferenceComponent<ReferenceValue>>(entity) != before,
            "Reference fields must advance their component's write version.");

        var system = new FieldSystem();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(system);
        systems.Update();
        Require(world.Read<ReferenceComponent<ReferenceValue>>(entity).Value == value && value.Amount == 1f,
            "Managed system and query functions must write the current referenced object.");
        Require(systems.GetSlot(0).LastSystemVersion == systems.GetSlot(0).CurrentSystemVersion,
            "Managed system updates must commit the successful system baseline.");

        world.AddBuffer<ReferenceElement<ReferenceValue>>(entity);
        world.ExecuteBufferWrite<ReferenceElement<ReferenceValue>>(entity, buffer =>
        {
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.SwapRemoveAt(0);
        });
        world.ExecuteBufferRead<ReferenceElement<ReferenceValue>>(entity, buffer =>
            Require(buffer.Count == 2 && buffer.Read(1).Value == value,
                "Managed generic buffer elements must retain references through growth and removal."));
        world.RemoveBuffer<ReferenceElement<ReferenceValue>>(entity);
    }

    private static void WriteFields(QueryCursor cursor, ref float amount)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            FieldWriteView<ReferenceComponent<ReferenceValue>, float, ReferenceAccess> fields =
                chunk.WriteFields<ReferenceComponent<ReferenceValue>, float, ReferenceAccess>();
            for (int row = 0; row < fields.Count; row++)
                fields.Write(row, in amount);
        }
    }

    private sealed class FieldSystem : ISystem<ImmediateSystemContext>
    {
        private QueryHandle _query;

        public void OnCreate(ref ImmediateSystemContext context) =>
            _query = context.World.Query(context.World.QueryDefinition().Write<ReferenceComponent<ReferenceValue>>());

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            float amount = 1f;
            context.World.ExecuteQuery(_query, context.LastSystemVersion, ref amount, &WriteFields);
        }

        public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_query);
    }

    private struct ReferenceComponent<T> : IComponent where T : class
    {
        public T Value;
    }

    private sealed class ReferenceValue
    {
        public float Amount { get; set; }
    }

    private struct ReferenceAccess : IFieldAccess<ReferenceComponent<ReferenceValue>, float>
    {
        public static float Read(in ReferenceComponent<ReferenceValue> component) => component.Value.Amount;
        public static void Write(ref ReferenceComponent<ReferenceValue> component, in float value) => component.Value.Amount = value;
    }

    [BufferCapacity(2)]
    private struct ReferenceElement<T> : IBufferElement where T : class
    {
        public T Value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class TypedFieldSmoke
{
    internal static void Check()
    {
        using var world = new World();
        var value = new ReferenceValue();
        Entity entity = world.Create(new ReferenceComponent<ReferenceValue> { Value = value });
        ulong before = world.ComponentDataWriteVersion<ReferenceComponent<ReferenceValue>>(entity);
        world.WriteField<ReferenceComponent<ReferenceValue>, float, ReferenceAccess>(entity, 0.5f);
        Require(world.ReadField<ReferenceComponent<ReferenceValue>, float, ReferenceAccess>(entity) == 0.5f,
            "Typed component fields support reference-bearing generic roots.");
        Require(world.ComponentDataWriteVersion<ReferenceComponent<ReferenceValue>>(entity) != before,
            "Reference fields advance their component write version.");

        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new FieldSystem());
        systems.Update();
        Require(world.Read<ReferenceComponent<ReferenceValue>>(entity).Value == value && value.Amount == 1f,
            "Typed system and query actions write the current referenced object.");
        Require(systems.GetState(0).LastSystemVersion == systems.GetState(0).CurrentSystemVersion,
            "System updates commit the successful observation version.");

        world.AddBuffer<ReferenceElement<ReferenceValue>>(entity);
        using (DynamicBuffer<ReferenceElement<ReferenceValue>> buffer = world.GetBuffer<ReferenceElement<ReferenceValue>>(entity))
        {
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.Add(new ReferenceElement<ReferenceValue> { Value = value });
            buffer.RemoveAtSwapBack(0);
        }
        using (ReadOnlyBuffer<ReferenceElement<ReferenceValue>> buffer = world.ReadBuffer<ReferenceElement<ReferenceValue>>(entity))
            Require(buffer.Count == 2 && buffer[1].Value == value,
                "Managed generic buffer elements retain references through growth and removal.");
        world.RemoveBuffer<ReferenceElement<ReferenceValue>>(entity);
    }

    private sealed class FieldSystem : ISystem<ImmediateSystemContext>
    {
        private Query _query;

        public void OnCreate(ref ImmediateSystemContext context) =>
            _query = context.World.Query(new QueryDescription().With<ReferenceComponent<ReferenceValue>>());

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            float amount = 1f;
            using QueryScope rows = _query.Since(context.LastSystemVersion).Open();
            foreach (QueryChunk chunk in rows)
            {
                Span<ReferenceComponent<ReferenceValue>> roots = chunk.Write<ReferenceComponent<ReferenceValue>>();
                for (int row = 0; row < roots.Length; row++)
                    ReferenceAccess.Write(ref roots[row], in amount);
            }
        }
    }

    private struct ReferenceComponent<T> : IComponent, ITrackedComponent where T : class
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

    private struct ReferenceElement<T> : IBufferElement where T : class
    {
        public T Value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal static unsafe class ViewTypes
{
    private static readonly Lock s_gate = new();
    private static ViewFunctions[] s_functions = new ViewFunctions[16];
    private static int s_count;

    internal static ushort Register(delegate*<World, Entity, void> buildFunction)
    {
        lock (s_gate)
        {
            DevelopmentCheck.Require(s_count < ushort.MaxValue, "The view type table has reached its type capacity.");
            int id = ++s_count;
            if (id == s_functions.Length)
                Array.Resize(ref s_functions, s_functions.Length * 2);
            s_functions[id] = new ViewFunctions(buildFunction);
            return (ushort)id;
        }
    }

    internal static void Build(World world, Entity entity)
    {
        ushort typeId = world.Read<ViewType>(entity).Id;
        Volatile.Read(ref s_functions)[typeId].BuildFunction(world, entity);
    }

    private readonly struct ViewFunctions(delegate*<World, Entity, void> buildFunction)
    {
        internal readonly delegate*<World, Entity, void> BuildFunction = buildFunction;
    }
}

internal static unsafe class ViewRegistration<T> where T : struct, IView, IEquatable<T>
{
    internal static readonly ushort Id = Register();

    private static ushort Register()
    {
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"View {typeof(T).Name} requires IComponent or ISparseComponent storage.");
        return ViewTypes.Register(&Build);
    }

    internal static void Build(World world, Entity entity)
    {
        T view = world.ReadComponentData<T>(entity);
        using var scope = Scope.Enter(world, entity);
        view.Build().Complete();
    }
}

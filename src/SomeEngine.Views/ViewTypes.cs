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

    internal static ushort Register(
        delegate*<World, Entity, void> buildFunction,
        delegate*<World, Entity, bool> parameterCheckFunction)
    {
        lock (s_gate)
        {
            DevelopmentCheck.Require(s_count < ushort.MaxValue, "The view type table has reached its type capacity.");
            int id = ++s_count;
            if (id == s_functions.Length)
                Array.Resize(ref s_functions, s_functions.Length * 2);
            s_functions[id] = new ViewFunctions(buildFunction, parameterCheckFunction);
            return (ushort)id;
        }
    }

    internal static void Build(World world, Entity entity)
    {
        ushort typeId = world.Read<ViewType>(entity).Id;
        Volatile.Read(ref s_functions)[typeId].BuildFunction(world, entity);
    }

    internal static bool HasParameterChanges(World world, Entity entity)
    {
        ushort typeId = world.Read<ViewType>(entity).Id;
        return Volatile.Read(ref s_functions)[typeId].ParameterCheckFunction(world, entity);
    }

    private readonly struct ViewFunctions(
        delegate*<World, Entity, void> buildFunction,
        delegate*<World, Entity, bool> parameterCheckFunction)
    {
        internal readonly delegate*<World, Entity, void> BuildFunction = buildFunction;
        internal readonly delegate*<World, Entity, bool> ParameterCheckFunction = parameterCheckFunction;
    }
}

internal static unsafe class ViewRegistration<T> where T : struct, IView, IEquatable<T>
{
    internal static readonly ushort Id = Register();

    private static ushort Register()
    {
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"View {typeof(T).Name} requires IComponent or ISparseComponent storage.");
        return ViewTypes.Register(&Build, &HasParameterChanges);
    }

    internal static void Build(World world, Entity entity)
    {
        if (world.Has<BuildRequest>(entity))
            world.RemoveTag<BuildRequest>(entity);
        T view = world.ReadComponentData<T>(entity);
        uint parameterVersion = T.Mode == BuildMode.Rerun ? world.ComponentDataWriteVersion<T>(entity) : 0;
        using var scope = Scope.Enter(world, entity, T.Mode == BuildMode.Rerun);
        view.Build().Complete();
        scope.Complete();
        if (T.Mode == BuildMode.Rerun)
            world.Replace(entity, new ParameterVersion(parameterVersion));
    }

    private static bool HasParameterChanges(World world, Entity entity) =>
        T.Mode == BuildMode.Rerun &&
        world.ComponentDataWriteVersion<T>(entity) != world.Read<ParameterVersion>(entity).Value;
}

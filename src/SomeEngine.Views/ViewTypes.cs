using System.Diagnostics;
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
        delegate*<World, Entity, ulong, void> buildFunction,
        delegate*<World, Entity, bool> parameterCheckFunction,
        ChildSource children)
    {
        lock (s_gate)
        {
            DevelopmentCheck.Require(s_count < ushort.MaxValue, "The view type table has reached its type capacity.");
            int id = ++s_count;
            if (id == s_functions.Length)
                Array.Resize(ref s_functions, s_functions.Length * 2);
            s_functions[id] = new ViewFunctions(buildFunction, parameterCheckFunction, children);
            return (ushort)id;
        }
    }

    internal static void Build(World world, Entity entity, ulong frame)
    {
        ushort typeId = world.Read<ViewType>(entity).Id;
        Volatile.Read(ref s_functions)[typeId].BuildFunction(world, entity, frame);
    }

    internal static bool HasParameterChanges(World world, Entity entity)
    {
        ushort typeId = world.Read<ViewType>(entity).Id;
        return Volatile.Read(ref s_functions)[typeId].ParameterCheckFunction(world, entity);
    }

    [Conditional("DEBUG")]
    internal static void CheckContent(World world, Entity entity)
    {
#if DEBUG
        ushort typeId = world.Read<ViewType>(entity).Id;
        DevelopmentCheck.Require(Volatile.Read(ref s_functions)[typeId].Children == ChildSource.Caller,
            "Content requires ChildSource.Caller; ChildSource.Build views declare children in their own Build.");
#endif
    }

    private readonly struct ViewFunctions
    {
        internal readonly delegate*<World, Entity, ulong, void> BuildFunction;
        internal readonly delegate*<World, Entity, bool> ParameterCheckFunction;
#if DEBUG
        internal readonly ChildSource Children;
#endif

        internal ViewFunctions(
            delegate*<World, Entity, ulong, void> buildFunction,
            delegate*<World, Entity, bool> parameterCheckFunction,
            ChildSource children)
        {
            BuildFunction = buildFunction;
            ParameterCheckFunction = parameterCheckFunction;
#if DEBUG
            Children = children;
#endif
        }
    }
}

internal static unsafe class ViewRegistration<T> where T : struct, IView, IEquatable<T>
{
    internal static readonly ushort Id = Register();

    private static ushort Register()
    {
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"View {typeof(T).Name} requires IComponent or ISparseComponent storage.");
        return ViewTypes.Register(&Build, &HasParameterChanges, T.Children);
    }

    internal static void Build(World world, Entity entity, ulong frame)
    {
        T view = world.ReadComponentData<T>(entity);
        uint parameterVersion = T.Mode == BuildMode.Rerun ? world.ComponentDataWriteVersion<T>(entity) : 0;
        try
        {
            using var scope = Scope.Enter(world, entity, frame,
                declaresChildren: T.Children == ChildSource.Build, dependenciesEnabled: T.Mode == BuildMode.Rerun);
            view.Build().Complete();
            scope.Complete();
            if (world.HasSparse<BuildError>(entity))
                world.RemoveSparse<BuildError>(entity);
        }
        catch (Exception exception)
        {
            var error = new BuildError { Exception = exception, Frame = frame };
            if (world.HasSparse<BuildError>(entity))
                world.ReplaceSparse(entity, in error);
            else
                world.AddSparse(entity, in error);
            DevelopmentCheck.Report($"View {typeof(T).Name} on {entity} failed to build in frame {frame}: {exception}");
        }
        if (T.Mode == BuildMode.Rerun)
            world.Replace(entity, new ParameterVersion(parameterVersion));
    }

    private static bool HasParameterChanges(World world, Entity entity) =>
        T.Mode == BuildMode.Rerun &&
        world.ComponentDataWriteVersion<T>(entity) != world.Read<ParameterVersion>(entity).Value;
}

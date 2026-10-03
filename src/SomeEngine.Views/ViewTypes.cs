using System.Diagnostics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;

namespace SomeEngine.Views;

internal static unsafe class ViewTypes
{
    private static readonly Lock s_gate = new();
    private static ViewFunctions[] s_functions = new ViewFunctions[16];
    private static int s_count;

    internal static ushort Register(
        delegate*<World, Entity, ulong, void> buildFunction,
        delegate*<World, Entity, bool> parameterCheckFunction,
        delegate*<World, Entity, ref LayoutPass, in Constraints, Size> measureFunction,
        ChildSource children)
    {
        lock (s_gate)
        {
            DevelopmentCheck.Require(s_count < ushort.MaxValue, "The view type table has reached its type capacity.");
            int id = ++s_count;
            if (id == s_functions.Length)
                Array.Resize(ref s_functions, s_functions.Length * 2);
            s_functions[id] = new ViewFunctions(buildFunction, parameterCheckFunction, measureFunction, children);
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

    internal static bool HasMeasure(World world, Entity entity) =>
        world.Has<ViewType>(entity) &&
        Volatile.Read(ref s_functions)[world.Read<ViewType>(entity).Id].MeasureFunction != null;

    internal static Size Measure(World world, Entity entity, ref LayoutPass pass, in Constraints constraints) =>
        Volatile.Read(ref s_functions)[world.Read<ViewType>(entity).Id].MeasureFunction(world, entity, ref pass, in constraints);

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
        internal readonly delegate*<World, Entity, ref LayoutPass, in Constraints, Size> MeasureFunction;
#if DEBUG
        internal readonly ChildSource Children;
#endif

        internal ViewFunctions(
            delegate*<World, Entity, ulong, void> buildFunction,
            delegate*<World, Entity, bool> parameterCheckFunction,
            delegate*<World, Entity, ref LayoutPass, in Constraints, Size> measureFunction,
            ChildSource children)
        {
            BuildFunction = buildFunction;
            ParameterCheckFunction = parameterCheckFunction;
            MeasureFunction = measureFunction;
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
        return ViewTypes.Register(&Build, &HasParameterChanges, default(T) is ILayout ? &Measure : null, T.Children);
    }

    internal static void Build(World world, Entity entity, ulong frame)
    {
        if (world.Has<BuildRequest>(entity))
            world.RemoveTag<BuildRequest>(entity);
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
            BuildErrors.Record(world, entity, exception, frame, typeof(T));
        }
        if (T.Mode == BuildMode.Rerun)
            world.Replace(entity, new ParameterVersion(parameterVersion));
    }

    private static bool HasParameterChanges(World world, Entity entity) =>
        T.Mode == BuildMode.Rerun &&
        world.ComponentDataWriteVersion<T>(entity) != world.Read<ParameterVersion>(entity).Value;

    private static Size Measure(World world, Entity entity, ref LayoutPass pass, in Constraints constraints)
    {
        T view = world.ReadComponentData<T>(entity);
        return view.Measure(ref pass, in constraints);
    }
}

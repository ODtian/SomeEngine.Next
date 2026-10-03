using System.Diagnostics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Diagnostics;
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

    internal static void RegisterSerializationId(ushort id, Guid serializationId)
    {
        lock (s_gate)
        {
            CheckSerializationId(id, serializationId);
            s_functions[id].SerializationId = serializationId;
        }
    }

    [Conditional("DEBUG")]
    private static void CheckSerializationId(ushort id, Guid serializationId)
    {
        DevelopmentCheck.Require(serializationId != Guid.Empty, "A view serialization id must identify a type.");
        DevelopmentCheck.Require(s_functions[id].SerializationId == Guid.Empty ||
            s_functions[id].SerializationId == serializationId, "A view type keeps one serialization id.");
        for (int index = 1; index <= s_count; index++)
            DevelopmentCheck.Require(index == id || s_functions[index].SerializationId != serializationId,
                "Each view type requires its own serialization id.");
    }

    internal static Guid GetSerializationId(ushort id)
    {
        Guid serializationId = Volatile.Read(ref s_functions)[id].SerializationId;
        DevelopmentCheck.Require(serializationId != Guid.Empty,
            "Register the view's generated serialization entry before writing its World.");
        return serializationId;
    }

    internal static ushort GetTypeId(Guid serializationId)
    {
        lock (s_gate)
        {
            for (int index = 1; index <= s_count; index++)
                if (s_functions[index].SerializationId == serializationId && serializationId != Guid.Empty)
                    return (ushort)index;
        }
        throw new InvalidDataException($"Unknown serialized view type {serializationId}.");
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

    private struct ViewFunctions
    {
        internal Guid SerializationId;
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
            SerializationId = default;
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
        return ViewTypes.Register(&BuildFromSystem, &HasParameterChanges, default(T) is ILayout ? &Measure : null, T.Children);
    }

    private static void BuildFromSystem(World world, Entity entity, ulong frame) =>
        Build(world, entity, frame, firstBuild: false);

    internal static void Build(World world, Entity entity, ulong frame, bool firstBuild)
    {
        ViewType viewType = world.Read<ViewType>(entity);
        if (viewType.RebuildRequired)
        {
            viewType.RebuildRequired = false;
            world.OverwriteComponentData(entity, in viewType);
        }
        if (world.Has<BuildRequest>(entity))
        {
            firstBuild = true;
            world.RemoveTag<BuildRequest>(entity);
        }
        T view = world.ReadComponentData<T>(entity);
        uint parameterVersion = T.Mode == BuildMode.Rerun ? world.ComponentDataWriteVersion<T>(entity) : 0;
        RebuildDiagnostics.Record<T>(world, entity, firstBuild, parameterVersion);
        if (Profiler.IsActive || EngineTrace.IsEnabled)
            BuildWithTracing(world, entity, frame, in view, parameterVersion);
        else
            BuildContents(world, entity, frame, in view, parameterVersion);
    }

    private static void BuildWithTracing(World world, Entity entity, ulong frame, in T view, uint parameterVersion)
    {
        using var profiler = Profiler.BeginScope(ViewTrace.Name);
        using var trace = EngineTrace.Begin(ViewTrace.Site);
        BuildContents(world, entity, frame, in view, parameterVersion);
    }

    private static void BuildContents(World world, Entity entity, ulong frame, in T view, uint parameterVersion)
    {
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
        {
            if (world.Has<ParameterVersion>(entity))
                world.Replace(entity, new ParameterVersion(parameterVersion));
            else
                world.Add(entity, new ParameterVersion(parameterVersion));
        }
    }

    private static bool HasParameterChanges(World world, Entity entity) =>
        T.Mode == BuildMode.Rerun &&
        world.ComponentDataWriteVersion<T>(entity) != world.Read<ParameterVersion>(entity).Value;

    private static Size Measure(World world, Entity entity, ref LayoutPass pass, in Constraints constraints)
    {
        T view = pass.Read<T>(entity);
        return view.Measure(ref pass, in constraints);
    }

    private static class ViewTrace
    {
        internal static readonly string Name = $"{typeof(T).FullName}.Build";
        internal static readonly TraceSite Site = new("view", Name);
    }
}

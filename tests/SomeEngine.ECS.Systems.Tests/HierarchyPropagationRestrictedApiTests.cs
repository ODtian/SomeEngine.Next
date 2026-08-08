using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class HierarchyPropagationRestrictedApiTests
{
    [Fact]
    public void ExplicitSuccessor_DoesNotFormARingWithDeferredPropagationPackets()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity(new EscapeValue());
            using var blockerStarted = new ManualResetEventSlim();
            using var releaseBlocker = new ManualResetEventSlim();
            using var successorStarted = new ManualResetEventSlim();
            JobHandle blocker = JobSystem.Schedule(
                new BlockingJob(blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(3)));

            HierarchyMaintenanceDependency<EscapeDomain> maintenance =
                HierarchyMaintenanceSystem<EscapeDomain>.ScheduleDependency(world, blocker);
            HierarchyPropagation propagation =
                HierarchyPropagationAdapter<EscapeDomain>.Schedule(
                    world,
                    [root],
                    new NoopPropagationJob(),
                    maintenance);
            JobHandle successor = HierarchyJobAccess<EscapeDomain>.ScheduleParentWrite(
                world,
                new SignalJob(successorStarted),
                propagation.Handle);

            releaseBlocker.Set();
            Assert.True(successorStarted.Wait(TimeSpan.FromSeconds(3)));
            successor.Complete();
            propagation.Handle.Complete();
        });
    }

    [Fact]
    public void EmptyPropagation_StillValidatesDeclaredComponentCapabilities()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            HierarchyMaintenanceDependency<EscapeDomain> maintenance =
                HierarchyMaintenanceSystem<EscapeDomain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            JobResourceAccess forgedAccess = WorldStorageJobResources.Read(
                world,
                new WorldStorageResourceKey(
                    WorldStorageKind.Table,
                    ComponentMetadata<ManagedEscapeValue>.Id));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                HierarchyPropagationAdapter<EscapeDomain>.Schedule(
                    world,
                    [],
                    new NoopPropagationJob(),
                    maintenance,
                    [forgedAccess]));

            Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(EscapeOperation.ReadOtherRoot)]
    [InlineData(EscapeOperation.ReplaceOtherRoot)]
    [InlineData(EscapeOperation.ExecutePrebuiltQuery)]
    [InlineData(EscapeOperation.GetOtherRootChildren)]
    [InlineData(EscapeOperation.GetWorldCommandBuffer)]
    [InlineData(EscapeOperation.RecordPrebuiltCommandBuffer)]
    [InlineData(EscapeOperation.BindPrebuiltHookView)]
    [InlineData(EscapeOperation.AcquireSystemTick)]
    [InlineData(EscapeOperation.AcquireSystemVersion)]
    public void PropagationCallback_RejectsEveryOrdinaryWorldEscapeBeforeSideEffects(
        EscapeOperation operation)
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity firstRoot = world.CreateEntity(new EscapeValue { Value = 11 });
            Entity secondRoot = world.CreateEntity(new EscapeValue { Value = 22 });
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<EscapeValue>());
            var prebuiltCommands = new CommandBuffer(world);
            ComponentHooks<EscapeValue> prebuiltHooks = world.Hooks<EscapeValue>();
            uint tickBefore = world.CurrentTick;
            EscapeState.Configure(
                world,
                firstRoot,
                secondRoot,
                query,
                prebuiltCommands,
                prebuiltHooks);

            HierarchyMaintenanceDependency<EscapeDomain> maintenance =
                HierarchyMaintenanceSystem<EscapeDomain>.ScheduleDependency(world);
            HierarchyPropagation propagation =
                HierarchyPropagationAdapter<EscapeDomain>.Schedule(
                    world,
                    [firstRoot, secondRoot],
                    new EscapingPropagationJob(operation),
                    maintenance,
                    [ComponentJobAccess<EscapeValue>.Write(world)],
                    new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => propagation.Handle.Complete());

            Assert.Contains(
                "HierarchyPropagationContext",
                error.Message,
                StringComparison.Ordinal);
            Assert.Equal(11, world.Read<EscapeValue>(firstRoot).Value);
            Assert.Equal(22, world.Read<EscapeValue>(secondRoot).Value);
            Assert.Equal(0, prebuiltCommands.CommandCount);
            // The rejected escape occurs before any admitted context.Write call. It must not
            // publish a change epoch or leave any data, command, or hook side effect.
            Assert.Equal(tickBefore, world.CurrentTick);

            // A rejected prebuilt hook mutation must not have installed the callback.
            world.Replace(secondRoot, new EscapeValue { Value = 23 });
            Assert.Equal(0, EscapeState.HookInvocations);
            prebuiltCommands.Dispose();
            EscapeState.Clear();
        });
    }

    private static void WithJobRuntime(Action action)
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            action();
        }
        finally
        {
            EscapeState.Clear();
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    public enum EscapeOperation : byte
    {
        ReadOtherRoot,
        ReplaceOtherRoot,
        ExecutePrebuiltQuery,
        GetOtherRootChildren,
        GetWorldCommandBuffer,
        RecordPrebuiltCommandBuffer,
        BindPrebuiltHookView,
        AcquireSystemTick,
        AcquireSystemVersion,
    }

    private readonly struct EscapingPropagationJob : IHierarchyPropagationJob<EscapeDomain>
    {
        private readonly EscapeOperation _operation;

        internal EscapingPropagationJob(EscapeOperation operation)
        {
            _operation = operation;
        }

        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            if (context.Entity != EscapeState.TriggerRoot)
                return;

            switch (_operation)
            {
                case EscapeOperation.ReadOtherRoot:
                    _ = EscapeState.World.Read<EscapeValue>(EscapeState.OtherRoot);
                    break;
                case EscapeOperation.ReplaceOtherRoot:
                    EscapeState.World.Replace(
                        EscapeState.OtherRoot,
                        new EscapeValue { Value = 999 });
                    break;
                case EscapeOperation.ExecutePrebuiltQuery:
                    EscapeState.World.ExecuteQuery(
                        EscapeState.Query,
                        static _ => throw new InvalidOperationException(
                            "The restricted query body must never execute."));
                    break;
                case EscapeOperation.GetOtherRootChildren:
                    _ = Hierarchy<EscapeDomain>.GetChildren(
                        EscapeState.World,
                        EscapeState.OtherRoot);
                    break;
                case EscapeOperation.GetWorldCommandBuffer:
                    EscapeState.World.Commands().Replace(
                        EscapeState.OtherRoot,
                        new EscapeValue { Value = 999 });
                    break;
                case EscapeOperation.RecordPrebuiltCommandBuffer:
                    EscapeState.Commands.Replace(
                        EscapeState.OtherRoot,
                        new EscapeValue { Value = 999 });
                    break;
                case EscapeOperation.BindPrebuiltHookView:
                    EscapeState.Hooks.OnReplace(EscapeState.OnReplace);
                    break;
                case EscapeOperation.AcquireSystemTick:
                    _ = EscapeState.World.AcquireSystemTick();
                    break;
                case EscapeOperation.AcquireSystemVersion:
                    _ = EscapeState.World.AcquireSystemVersion();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private readonly struct NoopPropagationJob : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            _ = context.Entity;
        }
    }

    private readonly struct BlockingJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingJob(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Propagation dependency release timed out.");
        }
    }

    private readonly struct SignalJob : IJob
    {
        private readonly ManualResetEventSlim _started;

        internal SignalJob(ManualResetEventSlim started)
        {
            _started = started;
        }

        public void Execute() => _started.Set();
    }

    private static class EscapeState
    {
        internal static World World { get; private set; } = null!;
        internal static Entity TriggerRoot { get; private set; }
        internal static Entity OtherRoot { get; private set; }
        internal static QueryHandle Query { get; private set; }
        internal static CommandBuffer Commands { get; private set; } = null!;
        internal static ComponentHooks<EscapeValue> Hooks { get; private set; } = null!;
        internal static int HookInvocations;

        internal static void Configure(
            World world,
            Entity triggerRoot,
            Entity otherRoot,
            QueryHandle query,
            CommandBuffer commands,
            ComponentHooks<EscapeValue> hooks)
        {
            World = world;
            TriggerRoot = triggerRoot;
            OtherRoot = otherRoot;
            Query = query;
            Commands = commands;
            Hooks = hooks;
            HookInvocations = 0;
        }

        internal static void Clear()
        {
            World = null!;
            TriggerRoot = default;
            OtherRoot = default;
            Query = default;
            Commands = null!;
            Hooks = null!;
            HookInvocations = 0;
        }

        internal static void OnReplace(
            DeferredWorld world,
            Entity entity,
            in EscapeValue value)
        {
            Interlocked.Increment(ref HookInvocations);
        }
    }

    private struct EscapeValue : IComponent
    {
        public int Value;
    }

    private struct ManagedEscapeValue : IComponent
    {
        public string? Value { get; set; }
    }

    private readonly struct EscapeDomain : IHierarchyDomain;
}

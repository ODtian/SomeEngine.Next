using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public class SystemGroupTests
{
    [Fact]
    public void Lifecycle_RunsCreateOnceUpdateEveryEnabledFrameAndDestroyOnce()
    {
        var log = new SystemEventLog();
        var driver = new RecordingDriver(log);
        var group = new SystemGroup<TestSystemContext>(driver);

        group.Add(new LifecycleSystem("A"));

        group.Update();
        group.Update();
        group.Dispose();
        group.Dispose();

        Assert.Equal(
            new[]
            {
                "driver:acquire:0:0",
                "driver:create:0:0:1",
                "driver:before:0",
                "system:create:A:0:1",
                "system:update:A:0:1",
                "driver:after:0",
                "driver:complete:0:1",
                "driver:acquire:0:1",
                "driver:create:0:1:2",
                "driver:before:0",
                "system:update:A:1:2",
                "driver:after:0",
                "driver:complete:0:2",
                "driver:create:0:2:2",
                "system:destroy:A:2:2",
                "driver:complete:0:2",
            },
            log.Events);
    }

    [Fact]
    public void Update_RunsSystemsInRegistrationOrder()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        group.Add(new OrderedSystem("A"));
        group.Add(new OrderedSystem("B"));
        group.Add(new OrderedSystem("C"));

        group.Update();

        Assert.Equal(new[] { "A", "B", "C" }, log.Events);
    }

    [Fact]
    public void DisabledSystem_IsSkippedAndKeepsSlotStateStable()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        int enabled = group.Add(new OrderedSystem("enabled"));
        int disabled = group.Add(new OrderedSystem("disabled"));
        group.Disable(disabled);

        group.Update();

        Assert.Equal(new[] { "enabled" }, log.Events);

        var enabledSlot = group.GetSlot(enabled);
        var disabledSlot = group.GetSlot(disabled);
        Assert.True(enabledSlot.Created);
        Assert.Equal(1u, enabledSlot.LastSystemVersion);
        Assert.False(disabledSlot.Created);
        Assert.False(disabledSlot.Enabled);
        Assert.Equal(0u, disabledSlot.LastSystemVersion);
        Assert.Equal(0u, disabledSlot.CurrentSystemVersion);
    }

    [Fact]
    public void StructSystem_PreservesFieldsAcrossUpdates()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        group.Add(new FieldPreservingStructSystem(initial: 5));

        group.Update();
        group.Update();

        Assert.Equal(new[] { "struct:6", "struct:7" }, log.Events);
    }

    [Fact]
    public void ClassSystem_PreservesPreconstructedInstanceState()
    {
        var log = new SystemEventLog();
        var system = new FieldPreservingClassSystem(initial: 10);
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        group.Add(system);

        group.Update();
        group.Update();

        Assert.Equal(12, system.Count);
        Assert.Equal(new[] { "class:11", "class:12" }, log.Events);
    }

    [Fact]
    public void VersionBaselines_AdvanceAcrossUpdates()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        group.Add(new VersionRecordingSystem());

        group.Update();
        group.Update();
        group.Update();

        Assert.Equal(
            new[] { "version:last=0:current=1", "version:last=1:current=2", "version:last=2:current=3" },
            log.Events);
    }

    [Fact]
    public void ImmediateSystemDriver_UsesWorldAcquireSystemTick()
    {
        var world = new World();
        var group = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var system = new ImmediateVersionSystem();

        group.Add(system);

        group.Update();
        group.Update();

        Assert.Equal(3u, world.CurrentTick);
        Assert.Equal(2u, group.GetSlot(0).LastSystemVersion);
    }

    [Fact]
    public void DriverHookOrder_IsDeterministic()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log));

        group.Add(new HookOrderSystem());

        group.Update();

        Assert.Equal(
            new[]
            {
                "driver:acquire:0:0",
                "driver:create:0:0:1",
                "driver:before:0",
                "system:create",
                "system:update",
                "driver:after:0",
                "driver:complete:0:1",
            },
            log.Events);
    }

    [Fact]
    public void SystemExceptions_BubbleWithoutBeingHidden()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log));

        group.Add(new ThrowingSystem());

        var ex = Assert.Throws<InvalidOperationException>(() => group.Update());
        Assert.Equal("boom", ex.Message);
        Assert.Equal(
            new[] { "driver:acquire:0:0", "driver:create:0:0:1", "driver:before:0" },
            log.Events);
    }

    [Fact]
    public void FirstUpdateException_DoesNotRunCreateAgain()
    {
        var log = new SystemEventLog();
        var group = new SystemGroup<TestSystemContext>(new RecordingDriver(log, recordDriverEvents: false));

        group.Add(new ThrowOnceAfterCreateSystem());

        Assert.Throws<InvalidOperationException>(() => group.Update());
        group.Update();
        group.Dispose();

        Assert.Equal(
            new[] { "create", "update:throw", "update:ok", "destroy" },
            log.Events);
    }

    private sealed class SystemEventLog
    {
        public List<string> Events { get; } = new();

        public void Add(string value)
        {
            Events.Add(value);
        }
    }

    private readonly struct TestSystemContext
    {
        public TestSystemContext(SystemEventLog log, uint lastSystemVersion, uint currentSystemVersion)
        {
            Log = log;
            LastSystemVersion = lastSystemVersion;
            CurrentSystemVersion = currentSystemVersion;
        }

        public SystemEventLog Log { get; }

        public uint LastSystemVersion { get; }

        public uint CurrentSystemVersion { get; }
    }

    private sealed class RecordingDriver : ISystemDriver<TestSystemContext>
    {
        private readonly SystemEventLog _log;
        private readonly bool _recordDriverEvents;
        private uint _nextVersion = 1;

        public RecordingDriver(SystemEventLog log, bool recordDriverEvents = true)
        {
            _log = log;
            _recordDriverEvents = recordDriverEvents;
        }

        public uint AcquireSystemVersion(ref SystemSlot slot)
        {
            if (_recordDriverEvents)
                _log.Add($"driver:acquire:{slot.Index}:{slot.LastSystemVersion}");

            return _nextVersion++;
        }

        public TestSystemContext CreateContext(ref SystemSlot slot)
        {
            if (_recordDriverEvents)
                _log.Add($"driver:create:{slot.Index}:{slot.LastSystemVersion}:{slot.CurrentSystemVersion}");

            return new TestSystemContext(_log, slot.LastSystemVersion, slot.CurrentSystemVersion);
        }

        public void BeforeUpdate(ref SystemSlot slot, ref TestSystemContext context)
        {
            if (_recordDriverEvents)
                _log.Add($"driver:before:{slot.Index}");
        }

        public void AfterUpdate(ref SystemSlot slot, ref TestSystemContext context)
        {
            if (_recordDriverEvents)
                _log.Add($"driver:after:{slot.Index}");
        }

        public void Complete(ref SystemSlot slot, ref TestSystemContext context)
        {
            if (_recordDriverEvents)
                _log.Add($"driver:complete:{slot.Index}:{slot.LastSystemVersion}");
        }
    }

    private struct LifecycleSystem : ISystem<TestSystemContext>
    {
        private readonly string _name;

        public LifecycleSystem(string name)
        {
            _name = name;
        }

        public void OnCreate(ref TestSystemContext context)
        {
            context.Log.Add($"system:create:{_name}:{context.LastSystemVersion}:{context.CurrentSystemVersion}");
        }

        public void OnUpdate(ref TestSystemContext context)
        {
            context.Log.Add($"system:update:{_name}:{context.LastSystemVersion}:{context.CurrentSystemVersion}");
        }

        public void OnDestroy(ref TestSystemContext context)
        {
            context.Log.Add($"system:destroy:{_name}:{context.LastSystemVersion}:{context.CurrentSystemVersion}");
        }
    }

    private readonly struct OrderedSystem : ISystem<TestSystemContext>
    {
        private readonly string _name;

        public OrderedSystem(string name)
        {
            _name = name;
        }

        public void OnUpdate(ref TestSystemContext context)
        {
            context.Log.Add(_name);
        }
    }

    private struct FieldPreservingStructSystem : ISystem<TestSystemContext>
    {
        private int _count;

        public FieldPreservingStructSystem(int initial)
        {
            _count = initial;
        }

        public void OnUpdate(ref TestSystemContext context)
        {
            _count++;
            context.Log.Add($"struct:{_count}");
        }
    }

    private sealed class FieldPreservingClassSystem : ISystem<TestSystemContext>
    {
        public FieldPreservingClassSystem(int initial)
        {
            Count = initial;
        }

        public int Count { get; private set; }

        public void OnUpdate(ref TestSystemContext context)
        {
            Count++;
            context.Log.Add($"class:{Count}");
        }
    }

    private readonly struct VersionRecordingSystem : ISystem<TestSystemContext>
    {
        public void OnUpdate(ref TestSystemContext context)
        {
            context.Log.Add($"version:last={context.LastSystemVersion}:current={context.CurrentSystemVersion}");
        }
    }

    private readonly struct ImmediateVersionSystem : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Assert.NotNull(context.World);
            Assert.True(context.CurrentSystemVersion > 0);
        }
    }

    private readonly struct HookOrderSystem : ISystem<TestSystemContext>
    {
        public void OnCreate(ref TestSystemContext context)
        {
            context.Log.Add("system:create");
        }

        public void OnUpdate(ref TestSystemContext context)
        {
            context.Log.Add("system:update");
        }
    }

    private readonly struct ThrowingSystem : ISystem<TestSystemContext>
    {
        public void OnUpdate(ref TestSystemContext context)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private struct ThrowOnceAfterCreateSystem : ISystem<TestSystemContext>
    {
        private bool _hasThrown;

        public void OnCreate(ref TestSystemContext context)
        {
            context.Log.Add("create");
        }

        public void OnUpdate(ref TestSystemContext context)
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                context.Log.Add("update:throw");
                throw new InvalidOperationException("boom");
            }

            context.Log.Add("update:ok");
        }

        public void OnDestroy(ref TestSystemContext context)
        {
            context.Log.Add("destroy");
        }
    }
}

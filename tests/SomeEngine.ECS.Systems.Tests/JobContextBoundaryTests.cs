using SomeEngine.ECS;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public class JobContextBoundaryTests
{
    [Fact]
    public void FakeJobContext_ChainsStronglyTypedPendingWorkInsideOneSystem()
    {
        var world = new World();
        var driver = new FakeJobSystemDriver(world);
        var group = new SystemGroup<FakeJobSystemContext>(driver);

        group.Add(new DoubleScheduleSystem());

        group.Update();

        FakeJobHandle pending = driver.GetPendingSnapshot(0);
        Assert.Equal(2, pending.Id);
        Assert.Equal("A>B", pending.Chain);
        Assert.Equal(2, driver.Jobs.ScheduledCount);
    }

    [Fact]
    public void FakeJobContext_IsolatesPendingWorkPerSystemSlot()
    {
        var world = new World();
        var driver = new FakeJobSystemDriver(world);
        var group = new SystemGroup<FakeJobSystemContext>(driver);

        group.Add(new NamedScheduleSystem("system-a"));
        group.Add(new NamedScheduleSystem("system-b"));

        group.Update();

        Assert.Equal("system-a", driver.GetPendingSnapshot(0).Chain);
        Assert.Equal("system-b", driver.GetPendingSnapshot(1).Chain);
        Assert.NotEqual(driver.GetPendingSnapshot(0).Id, driver.GetPendingSnapshot(1).Id);
    }

    [Fact]
    public void FakeJobContext_ExposesServicesAsStableDataButKeepsPendingOutOfServices()
    {
        var marker = new ServiceMarker();
        var services = new SingleServiceProvider(marker);
        var world = new World();
        var driver = new FakeJobSystemDriver(world, services);
        var group = new SystemGroup<FakeJobSystemContext>(driver);

        group.Add(new ServiceReadingScheduleSystem());

        group.Update();

        Assert.True(marker.WasRead);
        Assert.Null(services.GetService(typeof(FakeJobHandle)));
        Assert.Equal("service-job", driver.GetPendingSnapshot(0).Chain);
    }

    [Fact]
    public void BaseSystemsAssembly_HasNoJobPackageReference()
    {
        var references = typeof(SystemGroup<>).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, name =>
            name.Name == "SomeEngine.Job");

        Assert.Contains(references, name =>
            name.Name == "SomeEngine.ECS");
    }

    [Fact]
    public void FakeBoundary_DoesNotRequirePendingJobsAsAPropertyName()
    {
        Assert.Null(typeof(FakeJobSystemContext).GetProperty("PendingJobs"));
        Assert.NotNull(typeof(FakeJobSystemContext).GetProperty("Pending"));
    }

    private readonly struct DoubleScheduleSystem : ISystem<FakeJobSystemContext>
    {
        public void OnUpdate(ref FakeJobSystemContext context)
        {
            context.Pending = context.Jobs.Schedule("A", context.Pending);
            context.Pending = context.Jobs.Schedule("B", context.Pending);
        }
    }

    private readonly struct NamedScheduleSystem : ISystem<FakeJobSystemContext>
    {
        private readonly string _name;

        public NamedScheduleSystem(string name)
        {
            _name = name;
        }

        public void OnUpdate(ref FakeJobSystemContext context)
        {
            context.Pending = context.Jobs.Schedule(_name, context.Pending);
        }
    }

    private readonly struct ServiceReadingScheduleSystem : ISystem<FakeJobSystemContext>
    {
        public void OnUpdate(ref FakeJobSystemContext context)
        {
            var marker = (ServiceMarker?)context.Services.GetService(typeof(ServiceMarker));
            marker!.WasRead = true;
            context.Pending = context.Jobs.Schedule("service-job", context.Pending);
        }
    }

    private sealed class ServiceMarker
    {
        public bool WasRead { get; set; }
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly ServiceMarker _marker;

        public SingleServiceProvider(ServiceMarker marker)
        {
            _marker = marker;
        }

        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(ServiceMarker) ? _marker : null;
        }
    }

    private readonly struct FakeJobHandle
    {
        public FakeJobHandle(int id, string chain)
        {
            Id = id;
            Chain = chain;
        }

        public int Id { get; }

        public string Chain { get; }
    }

    private struct FakeJobScheduler
    {
        private int _nextId;

        public readonly int ScheduledCount => _nextId;

        public FakeJobHandle Schedule(string name, FakeJobHandle dependsOn)
        {
            int id = ++_nextId;
            string chain = string.IsNullOrEmpty(dependsOn.Chain)
                ? name
                : dependsOn.Chain + ">" + name;

            return new FakeJobHandle(id, chain);
        }
    }

    private struct FakeJobSystemSlot
    {
        public FakeJobHandle Pending;
    }

    private readonly struct FakeJobSystemContext
    {
        private readonly FakeJobSystemDriver _driver;
        private readonly int _slotIndex;

        public FakeJobSystemContext(
            FakeJobSystemDriver driver,
            int slotIndex,
            World world,
            IServiceProvider services,
            uint lastSystemVersion,
            uint currentSystemVersion)
        {
            _driver = driver;
            _slotIndex = slotIndex;
            World = world;
            Services = services;
            LastSystemVersion = lastSystemVersion;
            CurrentSystemVersion = currentSystemVersion;
        }

        public World World { get; }

        public IServiceProvider Services { get; }

        public uint LastSystemVersion { get; }

        public uint CurrentSystemVersion { get; }

        public ref FakeJobScheduler Jobs => ref _driver.Jobs;

        public ref FakeJobHandle Pending => ref _driver.GetPending(_slotIndex);
    }

    private sealed class FakeJobSystemDriver : ISystemDriver<FakeJobSystemContext>
    {
        private readonly World _world;
        private readonly IServiceProvider _services;
        private FakeJobScheduler _jobs;
        private FakeJobSystemSlot[] _jobSlots = new FakeJobSystemSlot[4];

        public FakeJobSystemDriver(World world, IServiceProvider? services = null)
        {
            _world = world;
            _services = services ?? new EmptyServiceProvider();
        }

        public ref FakeJobScheduler Jobs => ref _jobs;

        public uint AcquireSystemVersion(ref SystemSlot slot)
        {
            return _world.AcquireSystemTick();
        }

        public FakeJobSystemContext CreateContext(ref SystemSlot slot)
        {
            EnsureSlot(slot.Index);
            return new FakeJobSystemContext(
                this,
                slot.Index,
                _world,
                _services,
                slot.LastSystemVersion,
                slot.CurrentSystemVersion);
        }

        public ref FakeJobHandle GetPending(int slotIndex)
        {
            EnsureSlot(slotIndex);
            return ref _jobSlots[slotIndex].Pending;
        }

        public FakeJobHandle GetPendingSnapshot(int slotIndex)
        {
            EnsureSlot(slotIndex);
            return _jobSlots[slotIndex].Pending;
        }

        private void EnsureSlot(int slotIndex)
        {
            if ((uint)slotIndex < (uint)_jobSlots.Length)
                return;

            int newLength = _jobSlots.Length;
            while (slotIndex >= newLength)
                newLength *= 2;

            Array.Resize(ref _jobSlots, newLength);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}

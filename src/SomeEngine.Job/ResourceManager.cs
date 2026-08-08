using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private readonly JobRuntimeConfig _config;
    private readonly RuntimeCounters _counters;
    private readonly long _generation;
    private readonly Lock _sync = new();
    private readonly List<ResourceState?> _states = [null];
    private readonly Stack<int> _freeStates = new();
    private readonly Stack<ResourceAccessRegistrationData> _freeRegistrations = new();
    private readonly Stack<Dictionary<ResourceState, int>> _freeResourceStateMaps = new();
    private readonly Stack<HashSet<ResourceDependencyKey>> _freeResourceDependencySets = new();
    private JobSafetyMode _safetyMode = JobSafetyMode.Checked;

    internal ResourceManager(JobRuntimeConfig config, RuntimeCounters counters, long generation)
    {
        _config = config;
        _counters = counters;
        _generation = generation;
        _safetyMode = config.SafetyMode;
        _createContainerResourceBinding = CreateContainerResourceBinding;
    }

    internal JobSafetyMode SafetyMode
    {
        get
        {
            lock (_sync)
            {
                return _safetyMode;
            }
        }

        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (_sync)
            {
                _safetyMode = value;
            }
        }
    }

    internal JobResource CreateResource(string? name)
    {
        ResourceState state = CreateState(ResourceKind.Resource, name);
        return new JobResource(state.Id, state.Version, _generation);
    }

    internal JobResourceToken CreateToken(string? name)
    {
        ResourceState state = CreateState(ResourceKind.Token, name);
        return new JobResourceToken(state.Id, state.Version, _generation);
    }

    internal void Release(JobResource resource)
    {
        Release(resource.Id, resource.Version, resource.Generation, ResourceKind.Resource, fromScope: false);
    }

    internal void Release(JobResourceToken token)
    {
        Release(token.Id, token.Version, token.Generation, ResourceKind.Token, fromScope: false);
    }

    private Dictionary<ResourceState, int> RentResourceStateMap() =>
        _freeResourceStateMaps.Count == 0
            ? new Dictionary<ResourceState, int>()
            : _freeResourceStateMaps.Pop();

    private void ReturnResourceStateMap(Dictionary<ResourceState, int> map)
    {
        map.Clear();
        _freeResourceStateMaps.Push(map);
    }

    private HashSet<ResourceDependencyKey> RentResourceDependencySet() =>
        _freeResourceDependencySets.Count == 0
            ? new HashSet<ResourceDependencyKey>()
            : _freeResourceDependencySets.Pop();

    private void ReturnResourceDependencySet(HashSet<ResourceDependencyKey> set)
    {
        set.Clear();
        _freeResourceDependencySets.Push(set);
    }

}





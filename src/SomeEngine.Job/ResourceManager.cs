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
    private JobSafetyMode _safetyMode = JobSafetyMode.Checked;

    internal ResourceManager(JobRuntimeConfig config, RuntimeCounters counters, long generation)
    {
        _config = config;
        _counters = counters;
        _generation = generation;
        _safetyMode = config.SafetyMode;
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

}





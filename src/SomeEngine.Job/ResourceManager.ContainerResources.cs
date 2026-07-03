using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private readonly ConditionalWeakTable<object, ContainerResourceBinding> _containerResources = new();

    internal JobResourceToken GetContainerResourceToken(object container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return _containerResources.GetValue(container, CreateContainerResourceBinding).Token;
    }

    private ContainerResourceBinding CreateContainerResourceBinding(object container)
    {
        ResourceState state = CreateState(ResourceKind.Token, container.GetType().FullName);
        return new ContainerResourceBinding(this, new JobResourceToken(state.Id, state.Version, _generation));
    }

    private void TryReleaseContainerResourceToken(JobResourceToken token)
    {
        lock (_sync)
        {
            ResourceState? state = ResolveState(token.Id, token.Version, token.Generation, ResourceKind.Token);
            if (state is null || state.ActiveAccesses.Count != 0)
            {
                return;
            }

            state.Release();
            _freeStates.Push(token.Id);
        }
    }

    private sealed class ContainerResourceBinding
    {
        private readonly ResourceManager _owner;

        internal ContainerResourceBinding(ResourceManager owner, JobResourceToken token)
        {
            _owner = owner;
            Token = token;
        }

        internal JobResourceToken Token { get; }

        ~ContainerResourceBinding()
        {
            _owner.TryReleaseContainerResourceToken(Token);
        }
    }
}




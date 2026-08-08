using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private readonly ConditionalWeakTable<object, ContainerResourceBinding> _containerResources = new();
    private readonly ConditionalWeakTable<object, ContainerResourceBinding>.CreateValueCallback
        _createContainerResourceBinding;

    internal JobResourceToken GetContainerResourceToken(object container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return _containerResources.GetValue(container, _createContainerResourceBinding).Token;
    }

    private ContainerResourceBinding CreateContainerResourceBinding(object container)
    {
        ResourceState state = CreateState(ResourceKind.Token, container.GetType().FullName);
        return new ContainerResourceBinding(this, new JobResourceToken(state.Id, state.Version, _generation));
    }

    private bool TryReleaseContainerResourceToken(JobResourceToken token)
    {
        lock (_sync)
        {
            ResourceState? state = ResolveState(token.Id, token.Version, token.Generation, ResourceKind.Token);
            if (state is null)
            {
                return true;
            }
            if (state.ActiveAccesses.Count != 0 || state.PendingReservations != 0)
            {
                return false;
            }

            state.Release();
            _freeStates.Push(token.Id);
            return true;
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
            // The key may die while a job still owns (or merely reserves) its token. A
            // finalizer is one-shot, so explicitly retry on a later GC instead of leaking the
            // ResourceState forever after this transient condition.
            if (!_owner.TryReleaseContainerResourceToken(Token))
                GC.ReRegisterForFinalize(this);
        }
    }
}




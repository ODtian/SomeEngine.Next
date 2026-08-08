using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private const string CorruptResourceStatePoolMessage = "Resource state pool is corrupt.";
    private const string RangedFrontierUnderflowMessage = "Ranged resource frontier count underflowed.";
    internal void ReleaseScopeOwned(ReadOnlySpan<ScopeOwnedResource> resources)
    {
        if (resources.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (ScopeOwnedResource resource in resources)
            {
                ReleaseScopeOwnedCore(resource);
            }
        }
    }

    internal void ReleaseScopeOwned(IReadOnlyList<ScopeOwnedResource> resources)
    {
        if (resources.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            for (int i = 0; i < resources.Count; i++)
            {
                ReleaseScopeOwnedCore(resources[i]);
            }
        }
    }

    private void ReleaseScopeOwnedCore(ScopeOwnedResource resource)
    {
        ReleaseCore(resource.Id, resource.Version, resource.Generation, resource.Kind, fromScope: true);
    }

    private ResourceState CreateState(ResourceKind kind, string? name)
    {
        lock (_sync)
        {
            ResourceState state;
            int id;
            if (_freeStates.Count > 0)
            {
                id = _freeStates.Pop();
                state = _states[id] ?? throw new InvalidOperationException(CorruptResourceStatePoolMessage);
            }
            else
            {
                if (_states.Count > _config.MaxResourceStates)
                {
                    throw new InvalidOperationException(
                        $"Resource state capacity exhausted ({_config.MaxResourceStates}).");
                }

                id = _states.Count;
                state = new ResourceState(id);
                _states.Add(state);
                _counters.ResourceStateHighWater(id);
            }

            state.Reset(kind, name);
            return state;
        }
    }

    private void Release(int id, int version, long generation, ResourceKind kind, bool fromScope)
    {
        lock (_sync)
        {
            ReleaseCore(id, version, generation, kind, fromScope);
        }
    }

    private void ReleaseCore(int id, int version, long generation, ResourceKind kind, bool fromScope)
    {
        ResourceState? state = fromScope ? ResolveState(id, version, generation, kind) : ResolveForRelease(id, version, generation, kind);
        if (state is null)
        {
            return;
        }

        if (!fromScope &&
            (state.ActiveAccesses.Count != 0 || state.PendingReservations != 0) &&
            _safetyMode != JobSafetyMode.Fast)
        {
            throw CreateException(
                $"Cannot release {kind.ToString().ToLowerInvariant()} '{Describe(state)}' while it is in use.",
                state,
                jobType: null);
        }

        state.Release();
        _freeStates.Push(id);
    }

    private ResourceState? ResolveForAccess(JobResourceAccess access, Type jobType)
    {
        return ResolveOrThrow(
            access.Kind,
            access.Id,
            access.Version,
            access.Generation,
            jobType,
            ResourceResolveOperation.Access);
    }

    private ResourceState? ResolveForRelease(int id, int version, long generation, ResourceKind kind)
    {
        return ResolveOrThrow(
            kind,
            id,
            version,
            generation,
            jobType: null,
            ResourceResolveOperation.Release);
    }

    private ResourceState? ResolveOrThrow(
        ResourceKind kind,
        int id,
        int version,
        long generation,
        Type? jobType,
        ResourceResolveOperation operation)
    {
        ResourceState? state = ResolveState(id, version, generation, kind);
        if (state is not null)
        {
            return state;
        }

        if (_safetyMode == JobSafetyMode.Fast)
        {
            return null;
        }

        string invalidMessage = CreateInvalidOrStaleMessage(kind, jobType, operation);
        ResourceState? diagnosticState = FindStateForDiagnostics(id, version, generation, kind);
        if (diagnosticState is not null)
        {
            throw CreateException(invalidMessage, diagnosticState, jobType);
        }

        throw CreateException(
            invalidMessage,
            kind,
            id,
            jobType);
    }

    private static string CreateInvalidOrStaleMessage(
        ResourceKind kind,
        Type? jobType,
        ResourceResolveOperation operation)
    {
        string kindName = kind.ToString().ToLowerInvariant();
        return operation == ResourceResolveOperation.Access
            ? $"Invalid or stale {kindName} access for job '{jobType?.FullName}'."
            : $"Invalid or stale {kindName} release.";
    }

    private ResourceState? ResolveState(int id, int version, long generation, ResourceKind kind)
    {
        if (generation != _generation || id <= 0 || id >= _states.Count)
        {
            return null;
        }

        ResourceState? state = _states[id];
        if (state is null
            || !state.InUse
            || state.Version != version
            || state.Kind != kind)
        {
            return null;
        }

        return state;
    }

    private ResourceState? FindStateForDiagnostics(int id, int version, long generation, ResourceKind kind)
    {
        if (generation != _generation || id <= 0 || id >= _states.Count)
        {
            return null;
        }

        ResourceState? state = _states[id];
        if (state is null || state.Kind != kind)
        {
            return null;
        }

        if (state.InUse)
        {
            return state.Version == version ? state : null;
        }

        return state.ReleasedVersion == version ? state : null;
    }

    private JobResourceSafetyException CreateException(string message, ResourceState state, Type? jobType)
    {
        return new JobResourceSafetyException(
            message,
            _safetyMode,
            jobType?.FullName,
            state.Name,
            state.Id,
            state.Kind.ToString());
    }

    private JobResourceSafetyException CreateException(string message, ResourceKind kind, int resourceId, Type? jobType)
    {
        return new JobResourceSafetyException(
            message,
            _safetyMode,
            jobType?.FullName,
            resourceName: null,
            resourceId,
            kind.ToString());
    }

    private static string Describe(ResourceState state)
    {
        return state.Name ?? $"{state.Kind}#{state.Id}";
    }

    internal sealed class ResourceState
    {
        internal readonly int Id;
        internal readonly List<ActiveResourceAccess> ActiveAccesses = [];
        internal readonly List<ActiveResourceAccess> UnrangedReadersSinceLastWriter = [];
        internal readonly RangedResourceFrontier RangedAll = new();
        internal readonly RangedResourceFrontier RangedWriters = new();
        internal ActiveResourceAccess LastUnrangedWriter;
        internal int Version;
        internal int ReleasedVersion;
        internal int RangedAccessCount;
        internal int PendingReservations;
        internal bool InUse;
        internal bool HasLastUnrangedWriter;
        internal ResourceKind Kind;
        internal string? Name;

        internal ResourceState(int id)
        {
            Id = id;
        }

        internal void Reset(ResourceKind kind, string? name)
        {
            Version++;
            ReleasedVersion = 0;
            InUse = true;
            Kind = kind;
            Name = name;
            ActiveAccesses.Clear();
            PendingReservations = 0;
            ClearFrontier();
        }

        internal void Release()
        {
            ReleasedVersion = Version;
            Version++;
            InUse = false;
            ActiveAccesses.Clear();
            PendingReservations = 0;
            ClearFrontier();
        }

        internal void AddFrontierAccess(ActiveResourceAccess access)
        {
            if (access.Access.HasRange)
            {
                int rangedAccessCount = checked(RangedAccessCount + 1);
                RangedAll.Add(access);
                try
                {
                    if (access.Access.Mode != JobAccessMode.Read)
                        RangedWriters.Add(access);
                }
                catch
                {
                    RangedAll.Remove(access);
                    throw;
                }

                RangedAccessCount = rangedAccessCount;
                return;
            }

            if (access.Access.Mode == JobAccessMode.Read)
            {
                UnrangedReadersSinceLastWriter.Add(access);
                return;
            }

            LastUnrangedWriter = access;
            HasLastUnrangedWriter = true;
            UnrangedReadersSinceLastWriter.Clear();
        }

        internal void RemoveFrontierAccess(ActiveResourceAccess access)
        {
            if (access.Access.HasRange)
            {
                bool removed = RangedAll.Remove(access);
                bool removedWriter = access.Access.Mode == JobAccessMode.Read
                    || RangedWriters.Remove(access);
                Debug.Assert(removed == removedWriter, "Ranged resource frontier views diverged.");
                if (removed)
                {
                    Debug.Assert(RangedAccessCount > 0, RangedFrontierUnderflowMessage);
                    if (RangedAccessCount > 0)
                        RangedAccessCount--;
                }

                return;
            }

            if (access.Access.Mode == JobAccessMode.Read)
            {
                UnrangedReadersSinceLastWriter.Remove(access);
                return;
            }

            if (HasLastUnrangedWriter && LastUnrangedWriter.Equals(access))
            {
                RebuildUnrangedFrontierFromActiveAccesses();
            }
        }

        internal void RemoveOwnerAccesses(JobHandle owner)
        {
            bool removed = false;
            for (int i = ActiveAccesses.Count - 1; i >= 0; i--)
            {
                JobHandle activeOwner = ActiveAccesses[i].Owner;
                if (activeOwner.Index != owner.Index ||
                    activeOwner.Version != owner.Version ||
                    activeOwner.Generation != owner.Generation)
                {
                    continue;
                }

                ActiveAccesses.RemoveAt(i);
                removed = true;
            }

            if (removed)
                RebuildFrontierFromActiveAccesses();
        }

        private void RebuildFrontierFromActiveAccesses()
        {
            ClearFrontier();
            foreach (var active in ActiveAccesses)
            {
                AddFrontierAccess(active);
            }
        }

        private void RebuildUnrangedFrontierFromActiveAccesses()
        {
            ClearUnrangedFrontier();
            foreach (ActiveResourceAccess active in ActiveAccesses)
            {
                if (!active.Access.HasRange)
                    AddFrontierAccess(active);
            }
        }

        private void ClearFrontier()
        {
            RangedAll.Clear();
            RangedWriters.Clear();
            RangedAccessCount = 0;
            ClearUnrangedFrontier();
        }

        private void ClearUnrangedFrontier()
        {
            LastUnrangedWriter = default;
            HasLastUnrangedWriter = false;
            UnrangedReadersSinceLastWriter.Clear();
        }
    }

    internal readonly struct ActiveResourceAccess : IEquatable<ActiveResourceAccess>
    {
        internal readonly JobHandle Owner;
        internal readonly JobResourceAccess Access;
        internal readonly ResourceState State;

        internal ActiveResourceAccess(JobHandle owner, JobResourceAccess access, ResourceState state)
        {
            Owner = owner;
            Access = access;
            State = state;
        }

        public bool Equals(ActiveResourceAccess other)
        {
            return Owner.Index == other.Owner.Index
                && Owner.Version == other.Owner.Version
                && Owner.Generation == other.Owner.Generation
                && Access.Kind == other.Access.Kind
                && Access.Id == other.Access.Id
                && Access.Version == other.Access.Version
                && Access.Generation == other.Access.Generation
                && Access.Mode == other.Access.Mode
                && Access.HasRange == other.Access.HasRange
                && Access.RangeStart == other.Access.RangeStart
                && Access.RangeLength == other.Access.RangeLength
                && ReferenceEquals(State, other.State);
        }

        public override bool Equals(object? obj)
        {
            return obj is ActiveResourceAccess other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                HashCode.Combine(Owner.Index, Owner.Version, Owner.Generation),
                HashCode.Combine(Access.Id, Access.Version, Access.Generation),
                Access.Mode,
                Access.HasRange,
                Access.RangeStart,
                Access.RangeLength,
                RuntimeHelpers.GetHashCode(State));
        }
    }
}




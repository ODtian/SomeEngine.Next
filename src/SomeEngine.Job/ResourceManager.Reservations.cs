namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    internal ResourceAccessReservation ReserveAccesses(
        ReadOnlySpan<JobResourceAccess> accesses,
        Type jobType)
    {
        if (accesses.Length == 0)
            return ResourceAccessReservation.Empty;

        var reserved = new ReservedResourceAccess[accesses.Length];
        int count = 0;
        lock (_sync)
        {
            try
            {
                foreach (JobResourceAccess access in accesses)
                {
                    ResourceState? state = ResolveForAccess(access, jobType);
                    if (state is null)
                        continue;

                    state.PendingReservations = checked(state.PendingReservations + 1);
                    reserved[count++] = new ReservedResourceAccess(access, state);
                }
            }
            catch
            {
                ReleaseReservationCounts(reserved, count);
                throw;
            }
        }

        if (count == 0)
            return ResourceAccessReservation.Empty;

        return new ResourceAccessReservation(
            new ResourceAccessReservationData(reserved, count, jobType));
    }

    internal ResourceAccessRegistration ActivateReservation(
        JobHandle owner,
        ResourceAccessReservation reservation)
    {
        ResourceAccessReservationData? data = reservation.Data;
        if (data is null)
            return ResourceAccessRegistration.Empty;

        lock (_sync)
        {
            if (data.Status != ResourceReservationStatus.Reserved)
            {
                throw new InvalidOperationException(
                    "A deferred resource reservation can be activated only once.");
            }

            AccessBuilder<ActiveResourceAccess> registered = new();
            try
            {
                for (int i = 0; i < data.Count; i++)
                {
                    ReservedResourceAccess reserved = data.Accesses[i];
                    ResourceState? state = ResolveForAccess(reserved.Access, data.JobType);
                    if (state is null)
                        continue;

                    registered.Add(new ActiveResourceAccess(owner, reserved.Access, state));
                }
            }
            catch
            {
                registered.Clear();
                throw;
            }

            ResourceAccessRegistration registration = RegisterResolvedAccesses(registered);
            ConsumeReservation(data, ResourceReservationStatus.Activated);
            return registration;
        }
    }

    internal void CancelReservation(ResourceAccessReservation reservation)
    {
        ResourceAccessReservationData? data = reservation.Data;
        if (data is null)
            return;

        lock (_sync)
        {
            if (data.Status == ResourceReservationStatus.Reserved)
                ConsumeReservation(data, ResourceReservationStatus.Cancelled);
        }
    }

    private static void ConsumeReservation(
        ResourceAccessReservationData data,
        ResourceReservationStatus status)
    {
        ReleaseReservationCounts(data.Accesses, data.Count);
        Array.Clear(data.Accesses, 0, data.Count);
        data.Count = 0;
        data.Status = status;
    }

    private static void ReleaseReservationCounts(
        ReservedResourceAccess[] accesses,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            ReservedResourceAccess reserved = accesses[i];
            ResourceState state = reserved.State;
            JobResourceAccess access = reserved.Access;
            if (!state.InUse ||
                state.Id != access.Id ||
                state.Version != access.Version ||
                state.Kind != access.Kind ||
                state.PendingReservations <= 0)
            {
                continue;
            }

            state.PendingReservations--;
        }
    }

    internal readonly struct ReservedResourceAccess
    {
        internal ReservedResourceAccess(JobResourceAccess access, ResourceState state)
        {
            Access = access;
            State = state;
        }

        internal JobResourceAccess Access { get; }

        internal ResourceState State { get; }
    }
}

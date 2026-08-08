using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
namespace SomeEngine.ECS;

public partial class World
{
    public void AddBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        Buffers.Add<T>(entity);
    }

    public bool HasBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return Buffers.Has<T>(entity);
    }

    public void RemoveBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        Buffers.Remove<T>(entity);
    }

    /// <summary>
    /// Borrows a read-only buffer view for exactly the duration of <paramref name="execution"/>.
    /// The runtime holds World iteration ownership and the optional Job resource admission until
    /// the callback returns or faults.
    /// </summary>
    public void ExecuteBufferRead<T>(Entity entity, BufferReadExecution<T> execution)
        where T : struct, IBufferElement
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobBuffer<T>(WorldStorageAccess.Read);
        BeginStorageBorrow();
        try
        {
            execution(Buffers.BorrowRead<T>(entity));
        }
        catch (Exception bodyFault)
        {
            EndStorageBorrowAfterFault(bodyFault);
        }

        EndStorageBorrow();
    }

    /// <summary>
    /// Borrows a read-only buffer view with caller-owned state passed by reference, allowing a
    /// static callback on allocation-sensitive paths.
    /// </summary>
    public void ExecuteBufferRead<T, TState>(
        Entity entity,
        ref TState state,
        BufferReadExecution<T, TState> execution)
        where T : struct, IBufferElement
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobBuffer<T>(WorldStorageAccess.Read);
        BeginStorageBorrow();
        try
        {
            execution(Buffers.BorrowRead<T>(entity), ref state);
        }
        catch (Exception bodyFault)
        {
            EndStorageBorrowAfterFault(bodyFault);
        }

        EndStorageBorrow();
    }

    /// <summary>
    /// Borrows a writable dynamic buffer for exactly the duration of
    /// <paramref name="execution"/>. Direct ref/span access cannot outlive the runtime owner.
    /// </summary>
    public void ExecuteBufferWrite<T>(Entity entity, BufferWriteExecution<T> execution)
        where T : struct, IBufferElement
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobBuffer<T>(WorldStorageAccess.Write);
        BeginStorageBorrow();
        try
        {
            execution(Buffers.BorrowWrite<T>(entity));
        }
        catch (Exception bodyFault)
        {
            EndStorageBorrowAfterFault(bodyFault);
        }

        EndStorageBorrow();
    }

    /// <summary>
    /// Borrows a writable dynamic buffer with caller-owned state passed by reference, allowing a
    /// static callback on allocation-sensitive paths.
    /// </summary>
    public void ExecuteBufferWrite<T, TState>(
        Entity entity,
        ref TState state,
        BufferWriteExecution<T, TState> execution)
        where T : struct, IBufferElement
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobBuffer<T>(WorldStorageAccess.Write);
        BeginStorageBorrow();
        try
        {
            execution(Buffers.BorrowWrite<T>(entity), ref state);
        }
        catch (Exception bodyFault)
        {
            EndStorageBorrowAfterFault(bodyFault);
        }

        EndStorageBorrow();
    }

    private void EndStorageBorrowAfterFault(Exception bodyFault)
    {
        EndStorageBorrow();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFault).Throw();
    }
}


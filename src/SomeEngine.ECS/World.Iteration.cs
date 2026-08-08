namespace SomeEngine.ECS;

public partial class World
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void BeginIteration() => _iteration.Begin();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void BeginQueryBorrow() => _iteration.BeginQueryBorrow();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EndQueryBorrow() => _iteration.EndQueryBorrow();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void BeginStorageBorrow() => _iteration.BeginStorageBorrow();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EndStorageBorrow() => _iteration.EndStorageBorrow();

    private void EndIteration(bool completed)
    {
        bool rollback = _iteration.End(completed);
        if (_iteration.Active)
            return;

        try
        {
            if (rollback)
            {
                _hierarchy.RollbackDeferredWrites();
                _relationGraph.RollbackDeferredWrites(this);
                return;
            }

            try
            {
                _hierarchy.ValidateDeferredWrites();
                _relationGraph.ValidateDeferredWrites(this);
                _hierarchy.CommitDeferredWrites();
                _relationGraph.CommitDeferredWrites();
            }
            catch
            {
                _hierarchy.RollbackDeferredWrites();
                _relationGraph.RollbackDeferredWrites(this);
                throw;
            }
        }
        finally
        {
            _iteration.ReleaseOwner();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void BeginQueryIteration(bool relationshipWrite)
    {
        if (relationshipWrite)
            BeginIteration();
        else
            BeginQueryBorrow();
    }

    private void EndQueryIteration(bool relationshipWrite, bool completed)
    {
        if (relationshipWrite)
            EndIteration(completed);
        else
            EndQueryBorrow();
    }

    internal bool IsIterating => _iteration.HasOwner;

    internal void RequireRelationshipWriteOwner()
    {
        if (!_iteration.HasRelationshipWriteOwner)
        {
            throw new InvalidOperationException(
                "Writable relationship refs and spans are only valid inside World.ExecuteQuery " +
                "or World.ExecuteReadWrite. The runtime-owned callback defines their lifetime " +
                "and commits or rolls back topology as one owner scope.");
        }
    }

    internal void ThrowIfIterating()
    {
        _iteration.Throw();
    }
}


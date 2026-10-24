using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>Acquires typed component roots for one caller-defined batch, including sparse roots.</summary>
    /// <remarks>References obtained from the batch are used within this callback. Exposed roots receive one shared write version.</remarks>
    public unsafe void ExecuteComponentWrite<TComponent, TState>(ref TState state,
        delegate* managed<ComponentWriteBatch<TComponent>, ref TState, void> execution)
        where TComponent : struct, IComponent
        where TState : allows ref struct
    {
        DevelopmentCheck.Require(ComponentMetadata<TComponent>.AllowsPublicValueMutation,
            "Component batches require a component that permits direct value writes.");
        bool sparse = ComponentMetadata<TComponent>.Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope admission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table,
                ComponentMetadata<TComponent>.Id,
                WorldStorageAccess.Write));
        BeginStorageBorrow();
        try
        {
            execution(new ComponentWriteBatch<TComponent>(this, sparse, AcquireAdmittedSystemVersion()), ref state);
        }
        catch (Exception bodyFault)
        {
            EndStorageBorrowAfterFault(bodyFault);
        }
        EndStorageBorrow();
    }

    /// <summary>Reads a field through its typed component access, including reference-bearing roots.</summary>
    public TValue ReadField<TComponent, TValue, TAccess>(Entity entity)
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        int component = ComponentMetadata<TComponent>.Id;
        bool sparse = ComponentMetadata<TComponent>.Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope admission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table,
                component,
                WorldStorageAccess.Read));
        if (sparse)
            return TAccess.Read(in _sparse.ReadRef<TComponent>(entity));

        return TAccess.Read(in _components.ReadRef<TComponent>(entity));
    }

    /// <summary>Writes a typed field and advances the root component's write version.</summary>
    /// <remarks>The access resolves the current root and object path during this call.</remarks>
    public void WriteField<TComponent, TValue, TAccess>(Entity entity, in TValue value)
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        int component = ComponentMetadata<TComponent>.Id;
        DevelopmentCheck.Require(ComponentMetadata<TComponent>.AllowsPublicValueMutation,
            "Field writes require a component that permits direct value writes.");
        bool sparse = ComponentMetadata<TComponent>.Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope admission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table,
                component,
                WorldStorageAccess.Write));
        uint version = AcquireAdmittedSystemVersion();
        if (sparse)
        {
            TAccess.Write(ref _sparse.WriteRef<TComponent>(entity, version), in value);
            return;
        }

        TAccess.Write(ref _components.WriteRef<TComponent>(entity, version), in value);
    }
}

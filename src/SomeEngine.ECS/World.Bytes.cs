using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>Returns the first byte of an unmanaged component while preserving its write version.</summary>
    /// <remarks>Use the reference before a structural change or another operation replaces its storage.</remarks>
    public ref readonly byte ReadBytes(Entity entity, ushort component)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(component);
        return ref ReadBytes(entity, component, in info);
    }

    /// <summary>Returns a component region's first byte while preserving the component's write version.</summary>
    /// <remarks>The offset and length are checked in development builds. The reference has the same lifetime as the component bytes.</remarks>
    public ref readonly byte ReadBytes(Entity entity, ushort component, int offset, int length)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(component);
        DevelopmentCheck.Require(
            offset >= 0 && length >= 0 && offset <= info.Size && length <= info.Size - offset,
            "The byte offset and length must fit within the component size.");
        return ref Unsafe.Add(ref Unsafe.AsRef(in ReadBytes(entity, component, in info)), offset);
    }

    private ref readonly byte ReadBytes(Entity entity, ushort component, in ComponentInfo info)
    {
        ComponentByteChecks.Require(in info);
        using WorldJobAdmissionScope admission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                info.Storage == StoragePath.Sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table,
                component,
                WorldStorageAccess.Read));
        if (info.Storage == StoragePath.Sparse)
            return ref _sparse.ReadBytes(entity, component);

        return ref _components.ReadBytes(entity, component, in info);
    }

    /// <summary>Returns the first byte of an unmanaged component and advances its write version.</summary>
    /// <remarks>Use the reference before a structural change or another operation replaces its storage.</remarks>
    public ref byte WriteBytes(Entity entity, ushort component)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(component);
        return ref WriteBytes(entity, component, in info);
    }

    /// <summary>Returns a component region's first byte and advances the component's write version.</summary>
    /// <remarks>The offset and length are checked in development builds. The reference has the same lifetime as the component bytes.</remarks>
    public ref byte WriteBytes(Entity entity, ushort component, int offset, int length)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(component);
        DevelopmentCheck.Require(
            offset >= 0 && length >= 0 && offset <= info.Size && length <= info.Size - offset,
            "The byte offset and length must fit within the component size.");
        return ref Unsafe.Add(ref WriteBytes(entity, component, in info), offset);
    }

    private ref byte WriteBytes(Entity entity, ushort component, in ComponentInfo info)
    {
        ComponentByteChecks.Require(in info);
        DevelopmentCheck.Require(info.AllowsPublicValueMutation,
            "Component byte access requires a component that permits direct value writes.");
        using WorldJobAdmissionScope admission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                info.Storage == StoragePath.Sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table,
                component,
                WorldStorageAccess.Write));
        if (info.Storage == StoragePath.Sparse)
            return ref _sparse.WriteBytes(entity, component);

        return ref _components.WriteBytes(entity, component, in info);
    }
}

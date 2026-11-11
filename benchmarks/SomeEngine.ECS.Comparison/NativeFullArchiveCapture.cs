using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class NativeFullArchiveWorld<TValues>
    where TValues : struct, INativeArchiveValues
{
    public CapturedArchiveProgress CaptureProgress(bool preserve)
    {
        using var sparse = _sparse.OpenRead<SaveSparseRow>();
        ulong[] versions = (ulong[])Systems.Values.Clone(); if (!preserve) Array.Fill(versions, Version);
        return new CapturedProgress(_hierarchy.CopyProgress(Store.Used + 1), _relations.CopyProgress(Store.Used + 1), _shared.Values.AsSpan(0, _shared.Count).ToArray(),
            sparse.Entities.ToArray(), sparse.Values.ToArray(), (SaveResource)_resources.Read<SaveResource>().Clone(),
            new ArchiveResourceCopy(new NativeArchiveResources(_resources, _resourceCounter), _resourceCounter), versions,
            preserve ? Store.Destroyed.ToArray() : [], Store.DestroyedSequence, preserve ? _destroyedPosition : Store.DestroyedSequence);
    }
    private sealed class CapturedProgress(ReferenceHierarchy<Entity, TValues> hierarchy,
        ReferenceRelations<Entity, Entity, TValues, SaveEdge> relations, SaveShared[] shared,
        Entity[] sparseEntities, SaveSparseRow[] sparseValues, SaveResource resource, ArchiveResourceCopy typedResources, ulong[] versions,
        DestroyedEntity[] destroyed, ulong destroyedSequence, ulong destroyedPosition) : CapturedArchiveProgress
    {
        internal override void Restore(INativeArchiveProgress state, bool preserve)
        {
            var target = (NativeFullArchiveWorld<TValues>)state;
            target._hierarchy.RestoreProgress(hierarchy); target._relations.RestoreProgress(relations);
            target._shared = new(); foreach (SaveShared value in shared) target._shared.Intern(value);
            for (int i = 0; i < sparseEntities.Length; i++) target._sparse.Set(sparseEntities[i], sparseValues[i]);
            target._resources.Set((SaveResource)resource.Clone());
            ArchiveResources.Copy(typedResources.Values, new NativeArchiveResources(target._resources, target._resourceCounter));
            for (int i = 0; i < versions.Length; i++) target.Systems.Values[i] = preserve ? versions[i] : target.Version;
            target.Systems.RestoreVersions(target.Systems.Values);
            target.Store.Destroyed.Clear(); target.Store.Destroyed.AddRange(destroyed); target.Store.DestroyedSequence = destroyedSequence;
            target._destroyedPosition = destroyedPosition;
            target.RebuildIndexes();
            if (!preserve) target.RebaseChanges();
        }
        internal override void Write(ref DataWriter writer)
        {
            hierarchy.WriteProgress(ref writer, new SaveSchema.EntityCodec());
            relations.WriteProgress(ref writer, new SaveSchema.EntityCodec(), new SaveSchema.EntityCodec());
            writer.WriteInt32(shared.Length); foreach (SaveShared value in shared) new SaveSchema.SharedCodec().Write(ref writer, value);
            writer.WriteInt32(sparseEntities.Length);
            for (int i = 0; i < sparseEntities.Length; i++)
            {
                writer.WriteEntity(sparseEntities[i]); new SaveSchema.LinkCodec().Write(ref writer, sparseValues[i].Value);
                writer.WriteUInt64(sparseValues[i].Added); writer.WriteUInt64(sparseValues[i].Written);
            }
            new SaveSchema.ResourceCodec().Write(ref writer, resource);
            ArchiveResources.Write(typedResources.Values, ref writer);
            writer.WriteInt32(versions.Length); foreach (ulong value in versions) writer.WriteUInt64(value);
            WriteDestroyed(ref writer, destroyed, destroyedSequence, destroyedPosition);
        }
        public override void Dispose()
        {
            relations.Dispose(); resource.Dispose(); typedResources.Dispose();
            hierarchy = null!; relations = null!; resource = null!; typedResources = null!;
            shared = []; sparseEntities = []; sparseValues = []; versions = []; destroyed = [];
        }
    }
    private static void WriteDestroyed(ref DataWriter writer, IReadOnlyCollection<DestroyedEntity> values, ulong sequence, ulong position)
    {
        writer.WriteUInt64(sequence); writer.WriteUInt64(position); writer.WriteInt32(values.Count);
        foreach (DestroyedEntity value in values) { writer.WriteEntity(value.Entity); writer.WriteUInt64(value.Sequence); }
    }
}

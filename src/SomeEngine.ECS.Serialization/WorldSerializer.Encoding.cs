using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization;

public static partial class WorldSerializer
{
    private const int EntitiesPerRange = 256;

    private static void WriteWorldEntities(BinaryWriter writer, AdmittedWorldWrite admitted,
        WorldWritePlan capturePlan, IReadOnlyDictionary<Guid, int> manifestIndex,
        ISet<ExternalReferenceKey>? references)
    {
        writer.Write(admitted.LiveEntityCount);
        if (admitted.SlotCount <= EntitiesPerRange || !SupportsParallelWrite(capturePlan.Manifest))
        {
            WriteWorldRows(writer, admitted, capturePlan, manifestIndex, references, 0, admitted.SlotCount);
            return;
        }

        var rows = new EncodedRows[(admitted.SlotCount - 1) / EntitiesPerRange + 1];
        JobSystem.ExecuteParallel(new EncodeRows(admitted, capturePlan, manifestIndex, rows,
            references is not null), rows.Length);
        foreach (EncodedRows range in rows)
        {
            writer.Write(range.Bytes.Span);
            if (references is not null)
                foreach (ExternalReferenceKey reference in range.References!) references.Add(reference);
        }
    }

    private static bool SupportsParallelWrite(ReadOnlySpan<SerializationTypeRuntime> types)
    {
        foreach (SerializationTypeRuntime type in types)
            if (!type.SupportsParallelWrite) return false;
        return true;
    }

    private readonly record struct EncodedRows(ReadOnlyMemory<byte> Bytes, HashSet<ExternalReferenceKey>? References);

    private readonly struct EncodeRows(AdmittedWorldWrite admitted, WorldWritePlan capturePlan,
        IReadOnlyDictionary<Guid, int> manifestIndex, EncodedRows[] rows, bool collectReferences) : IJobParallelFor
    {
        public void Execute(int index)
        {
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes, SerializationBinary.StrictUtf8, leaveOpen: true);
            HashSet<ExternalReferenceKey>? references = collectReferences ? [] : null;
            int firstIndex = index * EntitiesPerRange;
            int indexLimit = Math.Min(admitted.SlotCount, firstIndex + EntitiesPerRange);
            WriteWorldRows(writer, admitted, capturePlan, manifestIndex, references, firstIndex, indexLimit);
            writer.Flush();
            rows[index] = new(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)), references);
        }
    }
}

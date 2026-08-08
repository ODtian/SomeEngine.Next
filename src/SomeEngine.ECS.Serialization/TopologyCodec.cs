using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

internal static class TopologyCodec
{
    internal static void ValidateWriteContract(
        SerializationRegistry registry,
        SerializationContract contract)
    {
        ReadOnlySpan<TopologySerializationRuntime> runtimes = registry.TopologyRuntimes;
        for (int i = 0; i < runtimes.Length; i++)
            runtimes[i].ValidateContract(contract);
    }

    internal static void ValidateWriteState(
        AdmittedWorldWrite admitted,
        SerializationRegistry registry)
    {
        ReadOnlySpan<TopologySerializationRuntime> runtimes = registry.TopologyRuntimes;
        for (int i = 0; i < runtimes.Length; i++)
            runtimes[i].ValidateWriteState(admitted);
    }

    /// <summary>
    /// Writes each admitted topology type directly from its final World backing. No topology DTO,
    /// preview generation, retained payload, or second adjacency/child array is created. Each
    /// value is encoded exactly once to the caller's stream and followed by its measured byte-count
    /// footer, including on non-seekable output. The caller validates every topology before its
    /// first output byte; this method then opens only the already-validated admitted backings.
    /// </summary>
    internal static void WriteAll(
        BinaryWriter writer,
        AdmittedWorldWrite admitted,
        SerializationRegistry registry,
        SerializationContract contract,
        long maximumRecords,
        long maximumPayloadBytes)
    {
        ValidateWriteContract(registry, contract);
        var recordBudget = new TopologyCaptureBudget(maximumRecords);
        var payloadBudget = new TopologyWriteBudget(maximumPayloadBytes);
        ReadOnlySpan<TopologySerializationRuntime> runtimes = registry.TopologyRuntimes;
        writer.Write(runtimes.Length);
        for (int i = 0; i < runtimes.Length; i++)
        {
            TopologySerializationRuntime runtime = runtimes[i];
            writer.Write((byte)runtime.Kind);
            PayloadFormat.WriteTypeKey(writer, runtime.TypeKey);
            WritePayload(
                writer,
                runtime,
                admitted,
                recordBudget,
                payloadBudget);
        }
    }

    private static void WritePayload(
        BinaryWriter writer,
        TopologySerializationRuntime runtime,
        AdmittedWorldWrite admitted,
        TopologyCaptureBudget recordBudget,
        TopologyWriteBudget budget)
    {
        writer.Flush();
        string stableName = runtime.TypeKey.StableName;
        using var output = new BoundedCountingWriteStream(
            writer.BaseStream,
            int.MaxValue,
            validateAppend: (currentBytes, appendBytes) =>
                budget.RequirePayloadAppend(currentBytes, appendBytes, stableName),
            limitExceeded: (_, _, _) => new InvalidOperationException(
                $"World serialization topology payload bytes exceed the Int32 wire limit while " +
                $"encoding '{stableName}'."));
        using (var payloadWriter = new BinaryWriter(
                   output,
                   SerializationBinary.StrictUtf8,
                   leaveOpen: true))
        {
            runtime.WriteAdmitted(payloadWriter, admitted, recordBudget);
            payloadWriter.Flush();
        }
        budget.AddPayloadBytes(output.BytesWritten, stableName);
        writer.Write(checked((int)output.BytesWritten));
    }

    internal static void ReadApply(
        BinaryReader reader,
        SerializationRegistry registry,
        SerializationReadBudget budget,
        SerializationContract contract,
        World world,
        IReferenceRemapper? remapper)
    {
        int count = budget.TopologyPayloadCount(reader.ReadInt32());
        ReadOnlySpan<TopologySerializationRuntime> expectedRuntimes = registry.TopologyRuntimes;
        if (count != expectedRuntimes.Length)
        {
            throw new InvalidDataException(
                $"Serialized topology runtime count {count} does not exactly match " +
                $"the registered runtime count {expectedRuntimes.Length}.");
        }

        for (int i = 0; i < count; i++)
        {
            var kind = (TopologySerializationKind)reader.ReadByte();
            if (kind != TopologySerializationKind.Hierarchy &&
                kind != TopologySerializationKind.Relation)
            {
                throw new InvalidDataException($"Unknown topology serialization kind {(byte)kind}.");
            }
            SerializationTypeKey key = PayloadFormat.ReadTypeKey(reader, budget);
            PayloadFormat.ValidateReadTypeKeyContract(contract, key);

            TopologySerializationRuntime runtime = registry.ResolveTopology(
                kind,
                key);
            if (!ReferenceEquals(runtime, expectedRuntimes[i]))
            {
                throw new InvalidDataException(
                    $"Serialized topology '{key.StableName}' is not in canonical registry order.");
            }
            runtime.ValidateContract(contract);

            using var payloadStream = new BoundedCountingReadStream(
                reader.BaseStream,
                budget.Limits.MaxPayloadBytes,
                limitExceeded: static (_, _, _) => new InvalidDataException(
                    "Topology payload exceeds the configured byte limit."));
            using (var payloadReader = new BinaryReader(
                       payloadStream,
                       SerializationBinary.StrictUtf8,
                       leaveOpen: true))
            {
                runtime.ReadApply(payloadReader, budget, world, remapper);
            }
            int declaredLength;
            try
            {
                declaredLength = reader.ReadInt32();
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"Topology payload '{key.StableName}' is missing its byte-count footer.",
                    exception);
            }
            if (payloadStream.BytesRead != declaredLength)
            {
                throw new InvalidDataException(
                    $"Topology payload '{key.StableName}' footer does not match the bytes consumed by its codec.");
            }
            budget.PayloadLength(declaredLength);
        }
    }

    private sealed class TopologyWriteBudget
    {
        private readonly long _maximumPayloadBytes;
        private long _payloadBytes;

        internal TopologyWriteBudget(long maximumPayloadBytes)
        {
            _maximumPayloadBytes = maximumPayloadBytes == 0
                ? long.MaxValue
                : maximumPayloadBytes;
        }

        internal void RequirePayloadAppend(
            long currentTypeBytes,
            int appendBytes,
            string stableName)
        {
            long typeBytes;
            long totalBytes;
            try
            {
                typeBytes = checked(currentTypeBytes + appendBytes);
                totalBytes = checked(_payloadBytes + typeBytes);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException(
                    $"World serialization topology payload byte count overflowed while encoding " +
                    $"'{stableName}'.",
                    exception);
            }

            if (totalBytes > _maximumPayloadBytes)
            {
                throw new InvalidOperationException(
                    $"World serialization topology payload bytes {totalBytes} exceed the configured " +
                    $"limit {_maximumPayloadBytes} while encoding '{stableName}'.");
            }
        }

        internal void AddPayloadBytes(long count, string stableName)
        {
            _payloadBytes = checked(_payloadBytes + count);
            if (_payloadBytes > _maximumPayloadBytes)
            {
                throw new InvalidOperationException(
                    $"World serialization topology payload bytes {_payloadBytes} exceed the configured " +
                    $"limit {_maximumPayloadBytes} while encoding '{stableName}'.");
            }
        }
    }

}

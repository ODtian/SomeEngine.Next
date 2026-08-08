using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

internal static class PayloadFormat
{
    public const uint Magic = 0x53434553; // "SECS"
    // Version 4 is the current-schema, single-encode wire. Item and topology payload lengths are
    // canonical footers, so codecs write directly to seekable and non-seekable sinks without a
    // measurement pass or encoded-memory staging.
    public const ushort Version = 4;

    public static void WriteHeader(
        BinaryWriter writer,
        SnapshotPayloadKind kind,
        ReadOnlySpan<SerializationTypeRuntime> manifest,
        SerializationContract contract)
    {
        ValidateContract(contract, manifest);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((byte)contract);
        writer.Write((byte)kind);
        WriteGuid(writer, contract == SerializationContract.RawCheckpoint
            ? SerializationEnvironment.RawCheckpointIdentity
            : Guid.Empty);
        writer.Write(manifest.Length);
        for (int i = 0; i < manifest.Length; i++)
            WriteTypeKey(writer, manifest[i].Entry.TypeKey);
    }

    public static (
        SnapshotPayloadKind Kind,
        SerializationTypeKey[] Manifest,
        SerializationContract Contract) ReadHeader(
            BinaryReader reader,
            SerializationReadBudget budget,
            SerializationContract? requiredContract = null)
    {
        var (kind, contract, manifestCount) = ReadHeaderPrefix(
            reader,
            budget,
            requiredContract);
        var manifest = new SerializationTypeKey[manifestCount];
        SerializationTypeKey previous = default;
        for (int i = 0; i < manifest.Length; i++)
        {
            manifest[i] = ReadCanonicalManifestTypeKey(
                reader,
                budget,
                contract,
                i,
                ref previous);
        }

        return (kind, manifest, contract);
    }

    internal static (
        SnapshotPayloadKind Kind,
        int ManifestCount,
        SerializationContract Contract) ReadHeaderManifestCount(
            BinaryReader reader,
            SerializationReadBudget budget,
            SerializationContract? requiredContract = null)
    {
        var (kind, contract, manifestCount) = ReadHeaderPrefix(
            reader,
            budget,
            requiredContract);
        SerializationTypeKey previous = default;
        for (int i = 0; i < manifestCount; i++)
            _ = ReadCanonicalManifestTypeKey(reader, budget, contract, i, ref previous);
        return (kind, manifestCount, contract);
    }

    private static (
        SnapshotPayloadKind Kind,
        SerializationContract Contract,
        int ManifestCount) ReadHeaderPrefix(
            BinaryReader reader,
            SerializationReadBudget budget,
            SerializationContract? requiredContract)
    {
        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Invalid SomeEngine.ECS serialization header magic.");

        ushort version = reader.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"Unsupported SomeEngine.ECS serialization format version {version}.");

        var contract = (SerializationContract)reader.ReadByte();
        if (contract != SerializationContract.RawCheckpoint &&
            contract != SerializationContract.DurableSave)
        {
            throw new InvalidDataException($"Unknown serialization contract {(byte)contract}.");
        }
        if (requiredContract is not null && contract != requiredContract.Value)
        {
            throw new InvalidDataException(
                $"Serialized payload contract is {contract}, but the caller requires {requiredContract.Value}.");
        }

        var kind = (SnapshotPayloadKind)reader.ReadByte();
        Guid checkpointIdentity = ReadGuid(reader);
        if (contract == SerializationContract.RawCheckpoint &&
            checkpointIdentity != SerializationEnvironment.RawCheckpointIdentity)
        {
            throw new InvalidDataException(
                "Raw checkpoint build/ABI identity does not match the current process. " +
                "Raw checkpoints are intentionally exact-build artifacts; use DurableSave for persistent data.");
        }
        if (contract == SerializationContract.DurableSave && checkpointIdentity != Guid.Empty)
            throw new InvalidDataException("Durable save header contains an invalid checkpoint identity.");

        int manifestCount = budget.ManifestCount(reader.ReadInt32());
        return (kind, contract, manifestCount);
    }

    private static SerializationTypeKey ReadValidatedTypeKey(
        BinaryReader reader,
        SerializationReadBudget budget,
        SerializationContract contract)
    {
        SerializationTypeKey key = ReadTypeKey(reader, budget);
        ValidateReadTypeKeyContract(contract, key);
        return key;
    }

    private static SerializationTypeKey ReadCanonicalManifestTypeKey(
        BinaryReader reader,
        SerializationReadBudget budget,
        SerializationContract contract,
        int ordinal,
        ref SerializationTypeKey previous)
    {
        SerializationTypeKey current = ReadValidatedTypeKey(reader, budget, contract);
        if (ordinal != 0)
        {
            if (current.StableId == previous.StableId)
            {
                throw new InvalidDataException(
                    $"Duplicate serialization manifest stable id {current.StableId}.");
            }
            if (SerializationRegistry.CompareTypeKeys(previous, current) >= 0)
            {
                throw new InvalidDataException(
                    "Serialization manifest type keys are not in canonical order.");
            }
        }
        previous = current;
        return current;
    }

    public static void WriteTypeKey(BinaryWriter writer, SerializationTypeKey key)
    {
        WriteGuid(writer, key.StableId);
        SerializationBinary.WriteString(writer, key.StableName);
        writer.Write(key.SchemaFingerprint);
    }

    public static SerializationTypeKey ReadTypeKey(
        BinaryReader reader,
        SerializationReadBudget budget)
    {
        Guid stableId = ReadGuid(reader);
        string stableName = SerializationBinary.ReadString(reader, budget, stableName: true);
        ulong schemaFingerprint = reader.ReadUInt64();
        return new SerializationTypeKey(stableId, stableName, schemaFingerprint);
    }

    internal static void ValidateContract(
        SerializationContract contract,
        ReadOnlySpan<SerializationTypeRuntime> manifest)
    {
        if (contract != SerializationContract.DurableSave)
            return;

        for (int i = 0; i < manifest.Length; i++)
        {
            SerializationTypeEntry entry = manifest[i].Entry;
            if (entry.CodecKind == ComponentCodecKind.Raw)
            {
                throw new InvalidOperationException(
                    $"Type '{entry.TypeKey.StableName}' uses an ABI-dependent implicit raw codec and cannot " +
                    "be written as DurableSave. Register a generated canonical codec or an explicit custom codec, " +
                    "or write an explicitly ABI-bound RawCheckpoint.");
            }

            if (entry.SchemaSource != SerializationSchemaSource.Explicit)
            {
                throw new InvalidOperationException(
                    $"Type '{entry.TypeKey.StableName}' uses a build-derived runtime schema identity and cannot " +
                    "be written as DurableSave. Register a source-generated canonical codec or supply an " +
                    "explicit stable SerializationTypeKey with the custom codec.");
            }

            if (entry.TypeKey.SchemaFingerprint == 0)
            {
                throw new InvalidOperationException(
                    $"Type '{entry.TypeKey.StableName}' does not declare an explicit 64-bit schema fingerprint " +
                    "and cannot be written as DurableSave. Supply a generated schema fingerprint or an " +
                    "explicit custom schema fingerprint.");
            }
        }
    }

    internal static void ValidateReadContract(
        SerializationContract contract,
        SerializationTypeRuntime runtime)
    {
        if (contract != SerializationContract.DurableSave)
            return;

        if (runtime.Entry.CodecKind == ComponentCodecKind.Raw)
        {
            throw new InvalidDataException(
                $"Durable save declares type '{runtime.Entry.TypeKey.StableName}' but the local registry " +
                "only provides an ABI-dependent implicit raw codec.");
        }

        if (runtime.Entry.SchemaSource != SerializationSchemaSource.Explicit)
        {
            throw new InvalidDataException(
                $"Durable save declares type '{runtime.Entry.TypeKey.StableName}' but the local registry " +
                "uses a build-derived runtime schema identity instead of an explicit stable schema key.");
        }

        if (runtime.Entry.TypeKey.SchemaFingerprint == 0)
        {
            throw new InvalidDataException(
                $"Durable save declares type '{runtime.Entry.TypeKey.StableName}' but the local registry " +
                "does not provide an explicit 64-bit schema fingerprint.");
        }
    }

    internal static void ValidateReadTypeKeyContract(
        SerializationContract contract,
        SerializationTypeKey key)
    {
        if (key.SchemaFingerprint == 0)
        {
            throw new InvalidDataException(
                $"Serialized type '{key.StableName}' does not declare a 64-bit schema fingerprint.");
        }
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitiveEncoding.WriteGuid(bytes, value);
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = reader.Read(bytes[offset..]);
            if (read == 0)
                throw new InvalidDataException("Truncated serialization header GUID.");
            offset += read;
        }
        return BinaryPrimitiveEncoding.ReadGuid(bytes);
    }
}

internal static class SerializationEnvironment
{
    internal static readonly Guid RawCheckpointIdentity = CreateRawCheckpointIdentity();

    private static Guid CreateRawCheckpointIdentity()
    {
        string identity = string.Join(
            "|",
            "SomeEngine.ECS.RawCheckpoint.v1",
            typeof(WorldSerializer).Assembly.ManifestModule.ModuleVersionId,
            typeof(World).Assembly.ManifestModule.ModuleVersionId,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture,
            IntPtr.Size,
            BitConverter.IsLittleEndian);
        return BinaryTypeId.FromLogicalName(identity);
    }
}

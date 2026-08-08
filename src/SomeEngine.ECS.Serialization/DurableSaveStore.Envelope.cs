using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public sealed partial class DurableSaveStore
{
    private const uint FormatVersion = 4;
    private const int HeaderSize = 64;
    private const int HashOffset = 32;
    private const int AuthenticationKindOffset = 14;
    private const int CopyBufferSize = 64 * 1024;

    private void WriteTemporaryFile(
        string temporaryPath,
        ulong generation,
        Action<Stream> writePayload)
    {
        using (var file = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None,
                   CopyBufferSize,
                   FileOptions.WriteThrough))
        {
            EnvelopeAuthenticationKind authenticationKind = WriteAuthenticationKind;
            WriteHeader(
                file,
                authenticationKind,
                generation,
                payloadLength: 0,
                default);

            long payloadLength;
            Digest256 envelopeHash;
            using (IncrementalHash hasher = CreateEnvelopeHasher(authenticationKind))
            {
                using (var payloadStream = new HashingWriteStream(
                           file,
                           hasher,
                           _options.MaximumPayloadBytes,
                           leaveOpen: true,
                           leaveHasherOpen: true))
                {
                    writePayload(payloadStream);
                    payloadLength = payloadStream.BytesWritten;
                }
                Observe(DurableSaveWriteStage.PayloadWritten);

                Span<byte> metadata = stackalloc byte[HashOffset];
                WriteHeaderMetadata(metadata, authenticationKind, generation, payloadLength);
                hasher.AppendData(metadata);
                envelopeHash = Digest256.Finish(hasher);
            }

            file.Position = 0;
            WriteHeader(file, authenticationKind, generation, payloadLength, envelopeHash);
            file.SetLength(checked(HeaderSize + payloadLength));
            file.Flush(flushToDisk: true);
            Observe(DurableSaveWriteStage.TemporaryFileFlushed);
        }
    }

    private SlotSet InspectSlots()
    {
        SlotInspection primary = InspectSlot(PrimaryPath);
        SlotInspection previous = InspectSlot(PreviousPath);
        return new SlotSet(primary, previous);
    }

    private SlotSet InspectVerifiedSlots()
    {
        SlotInspection primary = InspectSlot(PrimaryPath, verifyDigest: true);
        SlotInspection previous = InspectSlot(PreviousPath, verifyDigest: true);
        return new SlotSet(primary, previous);
    }

    private SlotInspection InspectSlot(string path)
    {
        return InspectSlot(path, verifyDigest: false);
    }

    private SlotInspection VerifyTemporaryFile(string path)
    {
        return InspectSlot(path, verifyDigest: true);
    }

    private SlotInspection InspectSlot(string path, bool verifyDigest)
    {
        try
        {
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);

            EnvelopeHeader header = ReadAndValidateHeader(file);
            ValidateFileLength(file, header.PayloadLength);
            if (verifyDigest)
            {
                Digest256 actualHash = ComputeEnvelopeHash(file, header);
                if (!actualHash.FixedTimeEquals(header.EnvelopeHash))
                {
                    return SlotInspection.Invalid(
                        path,
                        "The envelope digest does not match its metadata or payload.");
                }
            }

            if (header.Generation < _options.MinimumAcceptedGeneration)
            {
                return SlotInspection.Invalid(
                    path,
                    $"Generation {header.Generation} is below the configured anti-rollback floor " +
                    $"{_options.MinimumAcceptedGeneration}.");
            }

            return SlotInspection.Valid(path, header.Generation, header.PayloadLength);
        }
        catch (FileNotFoundException)
        {
            return SlotInspection.Missing(path);
        }
        catch (DirectoryNotFoundException)
        {
            return SlotInspection.Missing(path);
        }
        catch (InvalidDataException exception)
        {
            return SlotInspection.Invalid(path, exception.Message);
        }
        catch (EndOfStreamException exception)
        {
            return SlotInspection.Invalid(path, exception.Message);
        }
    }

    private EnvelopeHeader ReadAndValidateHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("The durable-save envelope magic is invalid.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported durable-save envelope version {version}.");

        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header[12..14]);
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"Invalid durable-save envelope header size {headerSize}.");

        var authenticationKind = (EnvelopeAuthenticationKind)header[AuthenticationKindOffset];
        if (authenticationKind != EnvelopeAuthenticationKind.Sha256 &&
            authenticationKind != EnvelopeAuthenticationKind.HmacSha256)
        {
            throw new InvalidDataException(
                $"Unknown durable-save envelope authentication kind {(byte)authenticationKind}.");
        }
        if (header[15] != 0)
            throw new InvalidDataException("The durable-save envelope reserved header byte is non-zero.");
        if (authenticationKind == EnvelopeAuthenticationKind.HmacSha256 && _authenticationKey is null)
        {
            throw new InvalidDataException(
                "The durable-save envelope requires an HMAC-SHA256 authentication key.");
        }
        if (authenticationKind == EnvelopeAuthenticationKind.Sha256 && _authenticationKey is not null)
        {
            throw new InvalidDataException(
                "The durable-save envelope is unauthenticated but this store requires HMAC-SHA256.");
        }

        ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        if (generation == 0)
            throw new InvalidDataException("A durable-save generation must be greater than zero.");

        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header[24..32]);
        if (payloadLength < 0 || payloadLength > _options.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Durable-save payload length {payloadLength} is outside the configured limit.");
        }

        return new EnvelopeHeader(
            authenticationKind,
            generation,
            payloadLength,
            Digest256.Read(header[HashOffset..]));
    }

    private static void ValidateFileLength(FileStream file, long payloadLength)
    {
        long expectedLength;
        try
        {
            expectedLength = checked(HeaderSize + payloadLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The durable-save payload length overflows the file envelope.", exception);
        }

        if (file.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Durable-save file length {file.Length} does not match the declared length {expectedLength}.");
        }
    }

    private Digest256 ComputeEnvelopeHash(Stream stream, EnvelopeHeader header)
    {
        stream.Position = HeaderSize;
        using var payload = new HashingReadStream(
            stream,
            header.PayloadLength,
            CreateEnvelopeHasher(header.AuthenticationKind));
        Span<byte> metadata = stackalloc byte[HashOffset];
        WriteHeaderMetadata(
            metadata,
            header.AuthenticationKind,
            header.Generation,
            header.PayloadLength);
        return payload.DrainAndCompleteDigest(metadata);
    }

    private static void RequireSynchronousWriter(Action<Stream> writePayload)
    {
        foreach (Delegate callback in writePayload.GetInvocationList())
        {
            if (callback.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
            {
                throw new ArgumentException(
                    "DurableSaveStore.Write requires a synchronous writer; async void callbacks " +
                    "can return before the payload is complete.",
                    nameof(writePayload));
            }
        }
    }

    private static void WriteHeader(
        Stream stream,
        EnvelopeAuthenticationKind authenticationKind,
        ulong generation,
        long payloadLength,
        Digest256 envelopeHash)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeaderMetadata(header[..HashOffset], authenticationKind, generation, payloadLength);
        envelopeHash.Write(header[HashOffset..]);
        stream.Write(header);
    }

    private static void WriteHeaderMetadata(
        Span<byte> destination,
        EnvelopeAuthenticationKind authenticationKind,
        ulong generation,
        long payloadLength)
    {
        if (destination.Length != HashOffset)
        {
            throw new ArgumentException(
                $"The envelope metadata must contain {HashOffset} bytes.",
                nameof(destination));
        }

        destination.Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..14], HeaderSize);
        destination[AuthenticationKindOffset] = (byte)authenticationKind;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], generation);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..32], payloadLength);
    }

    private IncrementalHash CreateEnvelopeHasher(EnvelopeAuthenticationKind authenticationKind) =>
        authenticationKind switch
        {
            EnvelopeAuthenticationKind.Sha256 when _authenticationKey is null =>
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            EnvelopeAuthenticationKind.HmacSha256 when _authenticationKey is not null =>
                IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _authenticationKey),
            EnvelopeAuthenticationKind.HmacSha256 => throw new InvalidDataException(
                "The durable-save envelope requires an HMAC-SHA256 authentication key."),
            EnvelopeAuthenticationKind.Sha256 => throw new InvalidDataException(
                "The durable-save envelope is unauthenticated but this store requires HMAC-SHA256."),
            _ => throw new InvalidDataException(
                $"Unknown durable-save envelope authentication kind {(byte)authenticationKind}."),
        };

    private static bool DrainAndVerify(HashingReadStream payload, EnvelopeHeader header)
    {
        Span<byte> metadata = stackalloc byte[HashOffset];
        WriteHeaderMetadata(
            metadata,
            header.AuthenticationKind,
            header.Generation,
            header.PayloadLength);
        Digest256 actualHash = payload.DrainAndCompleteDigest(metadata);
        return actualHash.FixedTimeEquals(header.EnvelopeHash);
    }

    private static FileStream OpenSlot(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            CopyBufferSize,
            FileOptions.SequentialScan);

    private static ReadOnlySpan<byte> Magic => "SEDSAVE4"u8;

    private EnvelopeAuthenticationKind WriteAuthenticationKind =>
        _authenticationKey is null
            ? EnvelopeAuthenticationKind.Sha256
            : EnvelopeAuthenticationKind.HmacSha256;

    private sealed record EnvelopeHeader(
        EnvelopeAuthenticationKind AuthenticationKind,
        ulong Generation,
        long PayloadLength,
        Digest256 EnvelopeHash);

    private enum EnvelopeAuthenticationKind : byte
    {
        Sha256 = 0,
        HmacSha256 = 1,
    }
}

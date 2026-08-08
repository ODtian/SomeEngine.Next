using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using SomeEngine.ECS;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

/// <summary>
/// A crash-resilient two-generation file store for durable-save payloads.
/// </summary>
/// <remarks>
/// Each commit is written and flushed to a temporary file in the destination directory,
/// verified, and then atomically published over the older generation. The newest valid
/// generation is never the publication target, so an interrupted commit leaves at least
/// one previously verified generation available. Unkeyed envelopes use SHA-256 to detect
/// corruption; when <see cref="DurableSaveStoreOptions.AuthenticationKey"/> is configured,
/// versioned envelopes instead use HMAC-SHA256 for integrity and authenticity. Neither mode
/// encrypts payloads or provides confidentiality.
/// </remarks>
public sealed partial class DurableSaveStore : IDisposable
{
    private readonly DurableSaveStoreOptions _options;
    private readonly byte[]? _authenticationKey;
    private readonly object _lifetimeGate = new();
    private readonly Dictionary<int, int> _operationThreads = new();
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private int _activeOperations;
    private bool _disposeRequested;
    private bool _disposed;

    public DurableSaveStore(string path, DurableSaveStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        PrimaryPath = Path.GetFullPath(path);
        PreviousPath = PrimaryPath + ".previous";
        _lockPath = PrimaryPath + ".lock";
        _directoryPath = Path.GetDirectoryName(PrimaryPath)
            ?? throw new ArgumentException("The durable-save path must have a parent directory.", nameof(path));

        DurableSaveStoreOptions suppliedOptions = options ?? new DurableSaveStoreOptions();
        if (suppliedOptions.MaximumPayloadBytes <= 0 || suppliedOptions.MaximumPayloadBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(DurableSaveStoreOptions.MaximumPayloadBytes)} must be between 1 and {int.MaxValue} bytes.");
        }

        if (suppliedOptions.AuthenticationKey is { } authenticationKey)
        {
            if (authenticationKey.IsEmpty)
            {
                throw new ArgumentException(
                    $"{nameof(DurableSaveStoreOptions.AuthenticationKey)} cannot be empty.",
                    nameof(options));
            }

            if (authenticationKey.Length < 32)
            {
                throw new ArgumentException(
                    $"{nameof(DurableSaveStoreOptions.AuthenticationKey)} must contain at least 32 bytes for HMAC-SHA256.",
                    nameof(options));
            }

            _authenticationKey = new byte[authenticationKey.Length];
            authenticationKey.Span.CopyTo(_authenticationKey);
        }

        // The options memory is a caller-owned borrow. The store acquires one independent key
        // backing at this explicit construction boundary and deterministically clears it after
        // all admitted operations finish.
        _options = new DurableSaveStoreOptions
        {
            MaximumPayloadBytes = suppliedOptions.MaximumPayloadBytes,
            MinimumAcceptedGeneration = suppliedOptions.MinimumAcceptedGeneration,
            WriteStageObserver = suppliedOptions.WriteStageObserver,
            AuthenticationKey = null,
        };
    }

    /// <summary>The first on-disk generation slot.</summary>
    public string PrimaryPath { get; }

    /// <summary>The second on-disk generation slot.</summary>
    public string PreviousPath { get; }

    /// <summary>
    /// Commits a payload produced by <paramref name="writePayload"/>.
    /// The callback must complete synchronously; disposing the supplied stream does not
    /// close the store's temporary file.
    /// </summary>
    public DurableSaveCommit Write(Action<Stream> writePayload)
    {
        using OperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(writePayload);
        return WriteCore(writePayload);
    }

    private DurableSaveCommit WriteCore(Action<Stream> writePayload)
    {
        RequireSynchronousWriter(writePayload);
        Directory.CreateDirectory(_directoryPath);

        using FileStream writeLock = OpenWriteLock();
        SlotSet slots = InspectVerifiedSlots();
        if (!slots.HasValidSlot && slots.HasExistingSlot)
            throw CreateNoValidGenerationException(slots);

        SlotInspection? latest = slots.Latest;
        ulong generationBaseline = latest?.Generation ?? _options.MinimumAcceptedGeneration;
        if (generationBaseline == ulong.MaxValue)
            throw new InvalidOperationException("The durable-save generation counter is exhausted.");

        // A brand-new store has no durable generation from which to advance. Treat the caller's
        // anti-rollback floor as that baseline so the first committed generation is immediately
        // admissible. Existing but rejected slots still fail closed above; silently jumping past
        // their generation would turn an apparent rollback into an accepted history rewrite.
        ulong generation = generationBaseline + 1;
        string targetPath = latest is null || !PathEquals(latest.Path, PrimaryPath)
            ? PrimaryPath
            : PreviousPath;
        string temporaryPath = CreateTemporaryPath();

        try
        {
            WriteTemporaryFile(temporaryPath, generation, writePayload);

            SlotInspection verification = VerifyTemporaryFile(temporaryPath);
            if (!verification.IsValid ||
                verification.Generation != generation)
            {
                throw new IOException(
                    $"The temporary durable-save generation failed verification: {verification.FailureReason}");
            }

            Observe(DurableSaveWriteStage.TemporaryFileVerified);
            Observe(DurableSaveWriteStage.BeforePublish);
            Publish(temporaryPath, targetPath);
            Observe(DurableSaveWriteStage.Published);

            return new DurableSaveCommit(generation, verification.PayloadLength, targetPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <summary>Serializes and commits a durable world snapshot.</summary>
    public DurableSaveCommit WriteWorld(
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        using OperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        return WriteCore(stream => WorldSerializer.WriteDurableWorld(stream, world, registry, options));
    }

    /// <summary>Reads and deserializes the highest valid durable world snapshot.</summary>
    public World ReadWorld(
        SerializationRegistry registry,
        WorldLoadOptions options = default)
    {
        using OperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(registry);
        return ReadWorldCore(registry, options);
    }

    private World ReadWorldCore(
        SerializationRegistry registry,
        WorldLoadOptions options)
    {
        const int maximumConcurrentWriteRetries = 3;

        for (int attempt = 0; attempt < maximumConcurrentWriteRetries; attempt++)
        {
            SlotSet slots = InspectSlots();
            if (slots.Latest is null)
            {
                if (!slots.HasExistingSlot)
                {
                    throw new FileNotFoundException(
                        "No durable-save generation exists.",
                        PrimaryPath);
                }

                throw CreateNoValidGenerationException(slots);
            }

            bool retryInspection = false;
            for (int candidateIndex = 0; candidateIndex < 2; candidateIndex++)
            {
                SlotInspection? candidate = slots.Latest;
                if (candidate is null)
                    break;

                CandidateWorldRead result = ReadWorldCandidate(candidate, registry, options);
                switch (result.Status)
                {
                    case CandidateReadStatus.Success:
                        return result.World!;
                    case CandidateReadStatus.RetryInspection:
                        retryInspection = true;
                        break;
                    case CandidateReadStatus.IntegrityFailure:
                        slots = slots.Reject(candidate.Path, result.FailureReason!);
                        continue;
                    default:
                        throw new InvalidOperationException("Unknown durable-save candidate read status.");
                }

                break;
            }

            if (!retryInspection)
                throw CreateNoValidGenerationException(slots);
        }

        throw new IOException("The durable-save generations changed repeatedly while they were being read.");
    }

    private CandidateWorldRead ReadWorldCandidate(
        SlotInspection candidate,
        SerializationRegistry registry,
        WorldLoadOptions options)
    {
        FileStream file;
        try
        {
            file = OpenSlot(candidate.Path);
        }
        catch (FileNotFoundException)
        {
            return CandidateWorldRead.Retry;
        }
        catch (DirectoryNotFoundException)
        {
            return CandidateWorldRead.Retry;
        }

        using (file)
        {
            EnvelopeHeader header;
            try
            {
                header = ReadAndValidateHeader(file);
                ValidateFileLength(file, header.PayloadLength);
            }
            catch (InvalidDataException exception)
            {
                return CandidateWorldRead.Integrity(exception.Message);
            }
            catch (EndOfStreamException exception)
            {
                return CandidateWorldRead.Integrity(exception.Message);
            }

            if (header.Generation != candidate.Generation)
                return CandidateWorldRead.Retry;

            using var payload = new HashingReadStream(
                file,
                header.PayloadLength,
                CreateEnvelopeHasher(header.AuthenticationKind));
            World? world = null;
            ExceptionDispatchInfo? decodeFailure = null;
            try
            {
                world = WorldSerializer.ReadDurableWorld(payload, registry, options);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                decodeFailure = ExceptionDispatchInfo.Capture(exception);
            }

            long decodedBytes = payload.Position;
            bool verified;
            try
            {
                verified = DrainAndVerify(payload, header);
            }
            catch (EndOfStreamException exception)
            {
                world?.Dispose();
                return CandidateWorldRead.Integrity(exception.Message);
            }
            catch (Exception verificationFailure)
            {
                if (world is null)
                    throw;
                WorldSerializer.RethrowAfterTemporaryWorldFailure(
                    verificationFailure,
                    world.Dispose);
                throw;
            }

            if (!verified)
            {
                world?.Dispose();
                return CandidateWorldRead.Integrity(
                    "The envelope digest does not match its metadata or payload.");
            }

            if (decodeFailure is not null)
            {
                world?.Dispose();
                decodeFailure.Throw();
            }

            if (decodedBytes != header.PayloadLength)
            {
                world!.Dispose();
                throw new InvalidDataException(
                    $"The durable-save decoder consumed {decodedBytes} of {header.PayloadLength} payload bytes.");
            }

            return CandidateWorldRead.Success(world!);
        }
    }

    /// <summary>
    /// Waits for in-flight synchronous operations, then deterministically clears the owned
    /// authentication key. Disposing from inside this store's own callback is rejected to avoid
    /// waiting on the current operation.
    /// </summary>
    public void Dispose()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;
            if (_operationThreads.ContainsKey(threadId))
            {
                throw new InvalidOperationException(
                    "DurableSaveStore cannot be disposed from inside one of its active callbacks.");
            }

            if (_disposeRequested)
            {
                while (!_disposed)
                    Monitor.Wait(_lifetimeGate);
                return;
            }

            _disposeRequested = true;
            while (_activeOperations != 0)
                Monitor.Wait(_lifetimeGate);

            if (_authenticationKey is not null)
                CryptographicOperations.ZeroMemory(_authenticationKey);
            _disposed = true;
            Monitor.PulseAll(_lifetimeGate);
        }
    }

    private FileStream OpenWriteLock()
    {
        try
        {
            return new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"Another writer is already committing durable save '{PrimaryPath}'.",
                exception);
        }
    }

    private static void Publish(string temporaryPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(
                temporaryPath,
                targetPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, targetPath);
    }

    private string CreateTemporaryPath() =>
        Path.Combine(
            _directoryPath,
            $".{Path.GetFileName(PrimaryPath)}.{Guid.NewGuid():N}.tmp");

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The published generations are authoritative; abandoned temporary files are ignored.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original commit failure when cleanup itself is denied.
        }
    }

    private static InvalidDataException CreateNoValidGenerationException(SlotSet slots) =>
        new(
            "No valid durable-save generation was found. " +
            $"Primary: {slots.Primary.FailureReason}; previous: {slots.Previous.FailureReason}.");

    private void Observe(DurableSaveWriteStage stage) =>
        _options.WriteStageObserver?.Invoke(stage);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private OperationLease EnterOperation()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_disposeRequested || _disposed)
                throw new ObjectDisposedException(nameof(DurableSaveStore));

            _activeOperations = checked(_activeOperations + 1);
            _operationThreads.TryGetValue(threadId, out int depth);
            _operationThreads[threadId] = checked(depth + 1);
            return new OperationLease(this, threadId);
        }
    }

    private void ExitOperation(int threadId)
    {
        lock (_lifetimeGate)
        {
            if (!_operationThreads.TryGetValue(threadId, out int depth) ||
                depth <= 0 ||
                _activeOperations <= 0)
            {
                throw new InvalidOperationException(
                    "DurableSaveStore operation scope is unbalanced or completed on another thread.");
            }

            if (depth == 1)
                _operationThreads.Remove(threadId);
            else
                _operationThreads[threadId] = depth - 1;
            _activeOperations--;
            if (_activeOperations == 0)
                Monitor.PulseAll(_lifetimeGate);
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private DurableSaveStore? _owner;
        private readonly int _threadId;

        internal OperationLease(DurableSaveStore owner, int threadId)
        {
            _owner = owner;
            _threadId = threadId;
        }

        public void Dispose()
        {
            DurableSaveStore? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ExitOperation(_threadId);
        }
    }

    private enum CandidateReadStatus : byte
    {
        Success,
        IntegrityFailure,
        RetryInspection,
    }

    private readonly record struct CandidateWorldRead(
        CandidateReadStatus Status,
        World? World,
        string? FailureReason)
    {
        internal static CandidateWorldRead Success(World world) =>
            new(CandidateReadStatus.Success, world, null);

        internal static CandidateWorldRead Integrity(string reason) =>
            new(CandidateReadStatus.IntegrityFailure, null, reason);

        internal static CandidateWorldRead Retry =>
            new(CandidateReadStatus.RetryInspection, null, null);
    }

    private sealed record SlotInspection(
        string Path,
        bool Exists,
        bool IsValid,
        ulong Generation,
        long PayloadLength,
        string FailureReason)
    {
        internal static SlotInspection Missing(string path) =>
            new(path, Exists: false, IsValid: false, 0, 0, "missing");

        internal static SlotInspection Invalid(string path, string reason) =>
            new(path, Exists: true, IsValid: false, 0, 0, reason);

        internal static SlotInspection Valid(string path, ulong generation, long payloadLength) =>
            new(path, Exists: true, IsValid: true, generation, payloadLength, "valid");
    }

    private sealed record SlotSet(SlotInspection Primary, SlotInspection Previous)
    {
        internal bool HasExistingSlot => Primary.Exists || Previous.Exists;
        internal bool HasValidSlot => Primary.IsValid || Previous.IsValid;

        internal SlotInspection? Latest
        {
            get
            {
                if (!Primary.IsValid)
                    return Previous.IsValid ? Previous : null;
                if (!Previous.IsValid)
                    return Primary;
                return Previous.Generation > Primary.Generation ? Previous : Primary;
            }
        }

        internal SlotSet Reject(string path, string reason)
        {
            if (PathEquals(path, Primary.Path))
                return new SlotSet(SlotInspection.Invalid(Primary.Path, reason), Previous);
            if (PathEquals(path, Previous.Path))
                return new SlotSet(Primary, SlotInspection.Invalid(Previous.Path, reason));
            throw new InvalidOperationException("The rejected durable-save path is not one of this store's slots.");
        }
    }

}

public sealed class DurableSaveStoreOptions
{
    /// <summary>Maximum payload accepted for writing or recovery. Defaults to 1 GiB.</summary>
    public long MaximumPayloadBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Optional caller-managed secret used to authenticate new envelopes with HMAC-SHA256.
    /// Supplying a key also requires authenticated envelopes when reading. Construction copies
    /// this read-only memory into store-owned secret storage. Disposal waits for active operations
    /// and then clears the store-owned copy. The store does not
    /// provide key generation, persistence, rotation, or secure key storage. Keys shorter than
    /// 32 bytes are rejected.
    /// </summary>
    public ReadOnlyMemory<byte>? AuthenticationKey { get; init; }

    /// <summary>
    /// Rejects otherwise valid generations below this caller-supplied anti-rollback floor.
    /// Persisting and monotonically advancing the floor is the caller's responsibility.
    /// </summary>
    public ulong MinimumAcceptedGeneration { get; init; }

    /// <summary>
    /// Optional commit-stage observer for telemetry and deterministic fault injection.
    /// Exceptions abort the write; at <see cref="DurableSaveWriteStage.Published"/> the
    /// new generation has already become durable and may be recovered by the next read.
    /// </summary>
    public Action<DurableSaveWriteStage>? WriteStageObserver { get; init; }
}

public enum DurableSaveWriteStage
{
    PayloadWritten,
    TemporaryFileFlushed,
    TemporaryFileVerified,
    BeforePublish,
    Published,
}

public sealed record DurableSaveCommit(
    ulong Generation,
    long PayloadLength,
    string PublishedPath);

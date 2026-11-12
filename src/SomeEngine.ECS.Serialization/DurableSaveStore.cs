using SomeEngine.Serialization;

using System.Security.Cryptography;

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
            AuthenticationKey = null,
        };
    }

    /// <summary>The first on-disk generation slot.</summary>
    public string PrimaryPath { get; }

    /// <summary>The second on-disk generation slot.</summary>
    public string PreviousPath { get; }

    /// <summary>Opens a streaming save that publishes when Commit completes.</summary>
    public SaveWrite BeginWrite() => new(this);

    public DurableSaveCommit Write(Stream payload)
    {
        using SaveWrite write = BeginWrite();
        payload.CopyTo(write.Stream);
        return write.Commit();
    }

    /// <summary>
    /// Waits for in-flight synchronous operations, then deterministically clears the owned
    /// authentication key. Disposing from inside this store's active write is rejected to avoid
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
                    "DurableSaveStore cannot be disposed from inside one of its active writes.");
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

    private static InvalidDataException CreateNoValidGenerationException(SaveFilesInspection slots) =>
        SerializationErrors.CreateDataException(
            slots.Primary.Exists ? slots.Primary.Error : slots.Previous.Error,
            "No valid durable-save generation was found. " +
            $"Primary: {slots.Primary.FailureReason}; previous: {slots.Previous.FailureReason}.");

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private SaveOperation EnterOperation()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_disposeRequested || _disposed)
                throw new ObjectDisposedException(nameof(DurableSaveStore));

            _activeOperations = checked(_activeOperations + 1);
            _operationThreads.TryGetValue(threadId, out int depth);
            _operationThreads[threadId] = checked(depth + 1);
            return new SaveOperation(this, threadId);
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

    private sealed class SaveOperation : IDisposable
    {
        private DurableSaveStore? _owner;
        private readonly int _threadId;

        internal SaveOperation(DurableSaveStore owner, int threadId)
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

    private readonly record struct CandidateFileRead(
        CandidateReadStatus Status,
        SaveRead? Read,
        string? FailureReason,
        SerializationError Error = SerializationError.Unspecified)
    {
        internal static CandidateFileRead Success(SaveRead read) =>
            new(CandidateReadStatus.Success, read, null);

        internal static CandidateFileRead Integrity(string reason, SerializationError error = SerializationError.IntegrityMismatch) =>
            new(CandidateReadStatus.IntegrityFailure, null, reason, error);

        internal static CandidateFileRead Retry =>
            new(CandidateReadStatus.RetryInspection, null, null);
    }

    private sealed record SaveFileInspection(
        string Path,
        bool Exists,
        bool IsValid,
        ulong Generation,
        long PayloadLength,
        string FailureReason,
        SerializationError Error = SerializationError.Unspecified)
    {
        internal static SaveFileInspection Missing(string path) =>
            new(path, Exists: false, IsValid: false, 0, 0, "missing");

        internal static SaveFileInspection Invalid(string path, string reason, SerializationError error = SerializationError.IntegrityMismatch) =>
            new(path, Exists: true, IsValid: false, 0, 0, reason, error);

        internal static SaveFileInspection Valid(string path, ulong generation, long payloadLength) =>
            new(path, Exists: true, IsValid: true, generation, payloadLength, "valid");
    }

    private sealed record SaveFilesInspection(SaveFileInspection Primary, SaveFileInspection Previous)
    {
        internal bool HasExistingSlot => Primary.Exists || Previous.Exists;
        internal bool HasValidSlot => Primary.IsValid || Previous.IsValid;

        internal SaveFileInspection? Latest
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

        internal SaveFilesInspection Reject(string path, string reason, SerializationError error)
        {
            if (PathEquals(path, Primary.Path))
                return new SaveFilesInspection(SaveFileInspection.Invalid(Primary.Path, reason, error), Previous);
            if (PathEquals(path, Previous.Path))
                return new SaveFilesInspection(Primary, SaveFileInspection.Invalid(Previous.Path, reason, error));
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

}

public sealed record DurableSaveCommit(
    ulong Generation,
    long PayloadLength,
    string PublishedPath);

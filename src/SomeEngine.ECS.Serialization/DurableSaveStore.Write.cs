using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public enum DurableSaveWriteStage
{
    PayloadWritten,
    TemporaryFileFlushed,
    TemporaryFileVerified,
    BeforePublish,
    Published,
}

public interface ISaveWriteStages
{
    void Execute(DurableSaveWriteStage stage);
}

public sealed partial class DurableSaveStore
{
    public sealed class SaveWrite : IDisposable
    {
        private readonly DurableSaveStore _store;
        private readonly SaveOperation _operation;
        private FileStream? _writeLock;
        private FileStream? _file;
        private IncrementalHash? _hasher;
        private HashingWriteStream? _payload;
        private readonly string _temporaryPath;
        private readonly string _targetPath;
        private readonly ulong _generation;
        private readonly EnvelopeAuthenticationKind _authenticationKind;
        private bool _disposed;

        internal SaveWrite(DurableSaveStore store)
        {
            _store = store;
            _operation = store.EnterOperation();
            _temporaryPath = store.CreateTemporaryPath();
            _targetPath = store.PrimaryPath;
            try
            {
                Directory.CreateDirectory(store._directoryPath);
                _writeLock = store.OpenWriteLock();
                SaveFilesInspection files = store.InspectVerifiedSlots();
                if (!files.HasValidSlot && files.HasExistingSlot)
                    throw CreateNoValidGenerationException(files);
                SaveFileInspection? latest = files.Latest;
                ulong generation = latest?.Generation ?? store._options.MinimumAcceptedGeneration;
                if (generation == ulong.MaxValue)
                    throw new InvalidOperationException("The durable-save generation counter is exhausted.");
                _generation = generation + 1;
                _targetPath = latest is null || !PathEquals(latest.Path, store.PrimaryPath)
                    ? store.PrimaryPath : store.PreviousPath;
                _file = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, CopyBufferSize, FileOptions.WriteThrough);
                _authenticationKind = store.WriteAuthenticationKind;
                WriteHeader(_file, _authenticationKind, _generation, 0, default);
                _hasher = store.CreateEnvelopeHasher(_authenticationKind);
                _payload = new HashingWriteStream(_file, _hasher, store._options.MaximumPayloadBytes,
                    leaveOpen: true, leaveHasherOpen: true);
            }
            catch { Dispose(); throw; }
        }

        public Stream Stream => _payload!;

        public DurableSaveCommit Commit()
        {
            var stages = new SaveWriteStages();
            return Commit(ref stages);
        }

        public DurableSaveCommit Commit<TStages>(ref TStages stages) where TStages : struct, ISaveWriteStages
        {
            stages.Execute(DurableSaveWriteStage.PayloadWritten);
            long length = _payload!.BytesWritten;
            _payload.Dispose();
            Span<byte> metadata = stackalloc byte[HashOffset];
            WriteHeaderMetadata(metadata, _authenticationKind, _generation, length);
            _hasher!.AppendData(metadata);
            Digest256 digest = Digest256.Finish(_hasher);
            _file!.Position = 0;
            WriteHeader(_file, _authenticationKind, _generation, length, digest);
            _file.SetLength(checked(HeaderSize + length));
            _file.Flush(flushToDisk: true);
            _file.Dispose();
            stages.Execute(DurableSaveWriteStage.TemporaryFileFlushed);
            SaveFileInspection verification = _store.VerifyTemporaryFile(_temporaryPath);
            if (!verification.IsValid || verification.Generation != _generation)
                throw new IOException($"The temporary save failed verification: {verification.FailureReason}");
            stages.Execute(DurableSaveWriteStage.TemporaryFileVerified);
            stages.Execute(DurableSaveWriteStage.BeforePublish);
            Publish(_temporaryPath, _targetPath);
            stages.Execute(DurableSaveWriteStage.Published);
            return new DurableSaveCommit(_generation, verification.PayloadLength, _targetPath);
        }

        private readonly struct SaveWriteStages : ISaveWriteStages
        {
            public void Execute(DurableSaveWriteStage stage) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _payload?.Dispose();
                _hasher?.Dispose();
                _file?.Dispose();
                TryDeleteTemporaryFile(_temporaryPath);
                _writeLock?.Dispose();
            }
            finally { _operation.Dispose(); }
        }
    }
}

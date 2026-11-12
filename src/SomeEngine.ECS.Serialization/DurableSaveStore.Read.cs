using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public sealed partial class DurableSaveStore
{
    public SaveRead BeginRead()
    {
        SaveOperation operation = EnterOperation();
        try { return ReadFileCore(operation); }
        catch { operation.Dispose(); throw; }
    }

    internal long ReadPayloadLength()
    {
        SaveFilesInspection slots = InspectSlots();
        return slots.Latest is { } latest ? latest.PayloadLength : throw CreateNoValidGenerationException(slots);
    }
    private SaveRead ReadFileCore(SaveOperation operation)
    {
        const int maximumConcurrentWriteRetries = 3;

        for (int attempt = 0; attempt < maximumConcurrentWriteRetries; attempt++)
        {
            SaveFilesInspection slots = InspectSlots();
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
                SaveFileInspection? candidate = slots.Latest;
                if (candidate is null)
                    break;

                CandidateFileRead result = OpenCandidate(candidate, operation);
                switch (result.Status)
                {
                    case CandidateReadStatus.Success:
                        return result.Read!;
                    case CandidateReadStatus.RetryInspection:
                        retryInspection = true;
                        break;
                    case CandidateReadStatus.IntegrityFailure:
                        slots = slots.Reject(candidate.Path, result.FailureReason!, result.Error);
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

    private CandidateFileRead OpenCandidate(SaveFileInspection candidate, SaveOperation operation)
    {
        FileStream file;
        try { file = OpenSlot(candidate.Path); }
        catch (FileNotFoundException) { return CandidateFileRead.Retry; }
        catch (DirectoryNotFoundException) { return CandidateFileRead.Retry; }
        bool transferred = false;
        try
        {
            EnvelopeHeader header;
            try
            {
                header = ReadAndValidateHeader(file);
                ValidateFileLength(file, header.PayloadLength);
            }
            catch (InvalidDataException exception)
            { return CandidateFileRead.Integrity(exception.Message, SerializationErrors.GetError(exception)); }
            catch (EndOfStreamException exception)
            { return CandidateFileRead.Integrity(exception.Message, SerializationError.IntegrityMismatch); }
            if (header.Generation != candidate.Generation) return CandidateFileRead.Retry;
            Digest256 actual;
            try { actual = ComputeEnvelopeHash(file, header); }
            catch (EndOfStreamException exception)
            { return CandidateFileRead.Integrity(exception.Message, SerializationError.IntegrityMismatch); }
            if (!actual.FixedTimeEquals(header.EnvelopeHash))
                return CandidateFileRead.Integrity("The envelope digest does not match its metadata or payload.");
            file.Position = HeaderSize;
            var read = new SaveRead(file, operation, header.Generation, header.PayloadLength);
            transferred = true;
            return CandidateFileRead.Success(read);
        }
        finally { if (!transferred) file.Dispose(); }
    }

    public sealed class SaveRead : IDisposable
    {
        private readonly Stream _stream;
        private IDisposable? _operation;
        public Stream Stream => _stream;
        public ulong Generation { get; }
        public long PayloadLength { get; }
        internal SaveRead(FileStream file, IDisposable operation, ulong generation, long length)
        { _stream = new BoundedCountingReadStream(file, long.MaxValue, leaveOpen: false); _operation = operation; Generation = generation; PayloadLength = length; }
        public void Dispose()
        {
            IDisposable? operation = Interlocked.Exchange(ref _operation, null);
            if (operation is null) return;
            try { _stream.Dispose(); }
            finally { operation.Dispose(); }
        }
    }
}

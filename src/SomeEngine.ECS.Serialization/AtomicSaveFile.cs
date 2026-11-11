using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization;

public interface ISaveDataWriter
{
    void Write(Stream output);
}

public static class AtomicSaveFile
{
    public static void Write<TWriter>(string path, in TWriter writer) where TWriter : struct, ISaveDataWriter
    {
        using var store = new DurableSaveStore(path);
        using var write = store.BeginWrite();
        writer.Write(write.Stream);
        write.Commit();
    }
    public static Stream OpenRead(string path) => new FileReadStream(path);
    internal static long PayloadLength(string path)
    {
        using var store = new DurableSaveStore(path);
        return store.ReadPayloadLength();
    }
    private sealed class FileReadStream : Stream
    {
        private readonly DurableSaveStore _store;
        private readonly DurableSaveStore.SaveRead _read;
        private readonly Stream _input;
        internal FileReadStream(string path)
        {
            _store = new(path);
            try { _read = _store.BeginRead(); _input = _read.Stream; }
            catch { _store.Dispose(); throw; }
        }
        public override bool CanRead => _input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _read.PayloadLength;
        public override long Position { get => _input.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _input.Read(buffer);
        public override int ReadByte() => _input.ReadByte();
        public override void Flush() => _input.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _read.Dispose(); } finally { _store.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
public sealed partial class WorldSerializer
{
    public void SaveFile(World world, string path, SavePurpose purpose = SavePurpose.DurableSave,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
        => SaveFile(world, path, new SerializeOptions(Purpose: purpose), progress, systems);

    public void SaveFile(World world, string path, SerializeOptions options,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
    {
        ValidateWriteOptions(options);
        AtomicSaveFile.Write(path, new WorldFileData(this, world, options, progress, systems));
    }

    public void LoadFile(World world, string path, SerializationReadLimits? limits = null, params ISystemVersions[] systems)
    {
        using Stream input = AtomicSaveFile.OpenRead(path);
        Load(world, input, limits, systems);
    }

    public Task SaveFileAsync(World world, string path, SavePurpose purpose = SavePurpose.DurableSave,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
        => SaveFileAsync(world, path, new SerializeOptions(Purpose: purpose), progress, systems);

    public Task SaveFileAsync(World world, string path, SerializeOptions options,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
    {
        Scheduler scheduler = JobSystem.Current;
        ValidateWriteOptions(options);
        var checkpoint = new WorldCheckpoint();
        try { checkpoint.Capture(world, registry, options, progress, systems); }
        catch { checkpoint.Dispose(); throw; }
        try
        {
            return scheduler.Schedule(new SaveFileJob(registry, checkpoint, path, options)).CompleteAsync().AsTask();
        }
        catch { checkpoint.Dispose(); throw; }
    }

    private readonly struct WorldFileData(WorldSerializer serializer, World world, SerializeOptions options,
        CheckpointProgress progress, ISystemVersions[] systems) : ISaveDataWriter
    {
        public void Write(Stream output) => serializer.Write(world, output, options, progress, systems);
    }

    private readonly struct CapturedFileData(SerializationRegistry registry, WorldCheckpoint checkpoint,
        SerializeOptions options) : ISaveDataWriter
    {
        public void Write(Stream output) => new WorldSerializer(registry).SaveCaptured(checkpoint, output, options);
    }

    private readonly struct SaveFileJob(SerializationRegistry registry, WorldCheckpoint checkpoint,
        string path, SerializeOptions options) : IJob
    {
        public void Execute()
        {
            try { AtomicSaveFile.Write(path, new CapturedFileData(registry, checkpoint, options)); }
            finally { checkpoint.Dispose(); }
        }
    }
}

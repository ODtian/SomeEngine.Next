using SomeEngine.Job;

namespace SomeEngine.Runtime;

public sealed partial record SceneImage
{
    /// <summary>Saves the packed pixels as a top-down 32-bit bitmap.</summary>
    public void SaveBmp(string path)
    {
        int rowBytes = checked(Width * 4);
        int imageBytes = checked(rowBytes * Height);
        byte[] bgra = new byte[imageBytes];
        JobSystem.ExecuteParallel(new ConvertBitmapRowsJob(Pixels, bgra, rowBytes), Height,
            JobScheduleOptions.AutomaticBatchSize);

        using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        writer.Write((ushort)0x4D42);
        writer.Write(checked(54 + imageBytes));
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(-Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(bgra);
    }

    private readonly struct ConvertBitmapRowsJob(ReadOnlyMemory<byte> pixels, byte[] bgra, int rowBytes) : IJobParallelFor
    {
        public void Execute(int row)
        {
            int offset = row * rowBytes;
            ReadOnlySpan<byte> source = pixels.Span.Slice(offset, rowBytes);
            Span<byte> destination = bgra.AsSpan(offset, rowBytes);
            for (int x = 0; x < rowBytes; x += 4)
            {
                destination[x] = source[x + 2];
                destination[x + 1] = source[x + 1];
                destination[x + 2] = source[x];
                destination[x + 3] = source[x + 3];
            }
        }
    }
}

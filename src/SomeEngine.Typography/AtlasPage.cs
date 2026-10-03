namespace SomeEngine.Typography;

/// <summary>A fixed-size, single-channel SDF page. Pixels are tightly packed in top-to-bottom rows.</summary>
public sealed class AtlasPage
{
    private readonly byte[] pixels;
    private int nextX = 1;
    private int nextY = 1;
    private int rowHeight;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => pixels;

    internal AtlasPage(int width, int height)
    {
        Width = width;
        Height = height;
        pixels = new byte[checked(width * height)];
    }

    internal bool TryAdd(int pageIndex, in GlyphBitmap bitmap, out AtlasDirtyRegion region)
    {
        int x = nextX;
        int y = nextY;
        int height = rowHeight;
        if (x + bitmap.Width + 1 > Width)
        {
            x = 1;
            y += height + 1;
            height = 0;
        }
        if (x + bitmap.Width + 1 > Width || y + bitmap.Height + 1 > Height)
        {
            region = default;
            return false;
        }

        for (int row = 0; row < bitmap.Height; row++)
            bitmap.Pixels.AsSpan(row * bitmap.Width, bitmap.Width)
                .CopyTo(pixels.AsSpan((y + row) * Width + x, bitmap.Width));
        nextX = x + bitmap.Width + 1;
        nextY = y;
        rowHeight = Math.Max(height, bitmap.Height);
        region = new AtlasDirtyRegion(pageIndex, x, y, bitmap.Width, bitmap.Height);
        return true;
    }

    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(nextX);
        writer.Write(nextY);
        writer.Write(rowHeight);
        writer.Write(pixels);
    }

    internal static AtlasPage ReadFrom(BinaryReader reader)
    {
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int x = reader.ReadInt32();
        int y = reader.ReadInt32();
        int rowHeight = reader.ReadInt32();
        if (width <= 2 || height <= 2 || x < 1 || x > width || y < 1 ||
            rowHeight < 0 || y > height - rowHeight - 1)
            throw new InvalidDataException("The atlas page dimensions and row positions must fit together.");
        var page = new AtlasPage(width, height) { nextX = x, nextY = y, rowHeight = rowHeight };
        reader.BaseStream.ReadExactly(page.pixels);
        return page;
    }
}

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ShadowImageFiles
{
    internal static Vector3[] ReadLinearPixels(string path, int width, int height)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int data = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22));
        Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)));
        Assert.Equal(height, Math.Abs(signedHeight));
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) / 8;
        Assert.Contains(channels, new[] { 3, 4 });
        int pitch = (width * channels + 3) & ~3;
        var pixels = new Vector3[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = data + (signedHeight < 0 ? y : height - 1 - y) * pitch + x * channels;
            pixels[y * width + x] = new(InverseToneMap(bytes[offset + 2]),
                InverseToneMap(bytes[offset + 1]), InverseToneMap(bytes[offset]));
        }
        return pixels;
    }

    internal static Vector3[] ReadRawLinearPixels(string path, int width, int height)
    {
        SceneLinearImage image = SceneLinearImage.Load(path);
        Assert.Equal(width, image.Frame.Width);
        Assert.Equal(height, image.Frame.Height);
        var pixels = new Vector3[width * height];
        for (int pixel = 0; pixel < pixels.Length; pixel++)
        {
            ReadOnlySpan<Half> rgba = image.Pixels.Span.Slice(pixel * 4, 4);
            pixels[pixel] = new((float)rgba[0], (float)rgba[1], (float)rgba[2]);
        }
        return pixels;
    }

    private static float InverseToneMap(byte encoded)
    {
        float mapped = MathF.Pow(encoded / 255f, 2.2f);
        return mapped / Math.Max(1 - mapped, 1e-6f);
    }

    internal static void WriteGrayscale(string path, int width, int height, byte[] pixels)
    {
        byte[] bytes = new byte[54 + pixels.Length * 4];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
        for (int pixel = 0; pixel < pixels.Length; pixel++)
        {
            bytes[54 + pixel * 4] = bytes[55 + pixel * 4] = bytes[56 + pixel * 4] = pixels[pixel];
            bytes[57 + pixel * 4] = 255;
        }
        File.WriteAllBytes(path, bytes);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(bytes, 54, bitmap.GetPixels(), pixels.Length * 4);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.ChangeExtension(path, ".png"));
        png.SaveTo(file);
    }
}

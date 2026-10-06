using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using SomeEngine.Graphics;
using Xunit;
using Xunit.Sdk;

namespace SomeEngine.Tests;

internal readonly record struct TestImage(int Width, int Height, Format Format, ReadOnlyMemory<byte> Bytes);

internal static class ImageArtifacts
{
    internal static string Root => Environment.GetEnvironmentVariable("SOMEENGINE_TEST_ARTIFACTS") ??
        Path.Combine("artifacts", "test-results");

    internal static void AssertEqual(TestImage expected, TestImage actual, [CallerMemberName] string test = "")
    {
        try
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(expected.Format, actual.Format);
            Assert.Equal(expected.Bytes.ToArray(), actual.Bytes.ToArray());
        }
        catch (XunitException)
        {
            SaveComparison(expected, actual, test);
            throw;
        }
    }

    internal static string SaveComparison(TestImage expected, TestImage actual, string test)
    {
        string directory = Path.Combine(Root, "images", $"{test}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        SavePng(Path.Combine(directory, "actual.png"), actual);
        SavePng(Path.Combine(directory, "expected.png"), expected);
        File.WriteAllBytes(Path.Combine(directory, "actual.raw"), actual.Bytes.ToArray());
        File.WriteAllBytes(Path.Combine(directory, "expected.raw"), expected.Bytes.ToArray());
        File.WriteAllText(Path.Combine(directory, "formats.txt"), $"actual: {actual.Width}x{actual.Height} {actual.Format}\nexpected: {expected.Width}x{expected.Height} {expected.Format}\n");
        int width = Math.Max(expected.Width, actual.Width), height = Math.Max(expected.Height, actual.Height);
        byte[] difference = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            difference[offset + 3] = 255;
            if (x >= expected.Width || y >= expected.Height || x >= actual.Width || y >= actual.Height)
            {
                difference[offset] = difference[offset + 2] = 255;
                continue;
            }
            float alpha = MathF.Abs(Channel(expected, x, y, 3) - Channel(actual, x, y, 3));
            for (int c = 0; c < 3; c++)
                difference[offset + c] = Preview(MathF.Max(alpha, MathF.Abs(Channel(expected, x, y, c) - Channel(actual, x, y, c))));
        }
        SavePng(Path.Combine(directory, "diff.png"), new(width, height, Format.R8G8B8A8UNorm, difference));
        Console.Error.WriteLine($"Image assertion artifacts: {Path.GetFullPath(directory)}");
        return directory;
    }

    internal static void SavePng(string path, TestImage image)
    {
        byte[] rgba = new byte[image.Width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        for (int c = 0; c < 4; c++)
            rgba[(y * image.Width + x) * 4 + c] = Preview(Channel(image, x, y, c));
        using var bitmap = new SKBitmap(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        png.SaveTo(file);
    }

    private static float Channel(TestImage image, int x, int y, int channel)
    {
        int pixel = y * image.Width + x;
        ReadOnlySpan<byte> bytes = image.Bytes.Span;
        return image.Format switch
        {
            Format.R8G8B8A8UNorm => bytes[pixel * 4 + channel] / 255f,
            Format.R16G16B16A16Float => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(pixel * 8 + channel * 2, 2))),
            Format.D32Float or Format.R32Float => channel == 3 ? 1 : BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(pixel * 4, 4)),
            _ => throw new NotSupportedException($"Image evidence does not support {image.Format}."),
        };
    }

    private static byte Preview(float value) => float.IsFinite(value) ? (byte)Math.Clamp(value * 255, 0, 255) : (byte)255;
}

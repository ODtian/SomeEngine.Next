using System.Runtime.CompilerServices;
using SkiaSharp;
using Xunit.Sdk;

namespace SomeEngine.Runtime.Tests;

internal static class SceneImages
{
    internal static async Task RunAsync(string backend, SceneScript script, params string[] options)
    {
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, .. options], script);
        Assert.True(result.Succeeded, $"{backend}: {result.Failure}, completed frames={result.CompletedFrames}; {result.Error}");
    }

    internal static void AssertEqual(SceneImage expected, SceneImage actual, int tolerance = 0,
        [CallerMemberName] string test = "")
    {
        try
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(expected.Pixels.Length, actual.Pixels.Length);
            int maximum = 0, largestErrorIndex = 0;
            for (int index = 0; index < actual.Pixels.Length; index++)
            {
                int difference = Math.Abs(expected.Pixels.Span[index] - actual.Pixels.Span[index]);
                if (difference > maximum)
                {
                    maximum = difference;
                    largestErrorIndex = index;
                }
            }
            int x = largestErrorIndex / 4 % actual.Width;
            int y = largestErrorIndex / 4 / actual.Width;
            Assert.True(maximum <= tolerance,
                $"Frame {actual.Frame}: maximum RGBA8 error {maximum}, tolerance {tolerance}; " +
                $"pixel ({x}, {y}), channel {largestErrorIndex % 4}: " +
                $"expected {expected.Pixels.Span[largestErrorIndex]}, actual {actual.Pixels.Span[largestErrorIndex]}.");
        }
        catch (XunitException)
        {
            SaveFailure(expected, actual, test);
            throw;
        }
    }

    internal static void AssertNotEqual(SceneImage expected, SceneImage actual,
        [CallerMemberName] string test = "")
    {
        try { Assert.NotEqual(expected.Pixels.ToArray(), actual.Pixels.ToArray()); }
        catch (XunitException) { SaveFailure(expected, actual, test); throw; }
    }

    internal static void AssertEqualRegion(SceneImage expected, SceneImage actual, int left, int top, int width, int height,
        [CallerMemberName] string test = "")
    {
        try
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            for (int channel = 0; channel < 4; channel++)
            {
                int pixel = (y * actual.Width + x) * 4 + channel;
                Assert.Equal(expected.Pixels.Span[pixel], actual.Pixels.Span[pixel]);
            }
        }
        catch (XunitException)
        {
            SaveFailure(expected, actual, test);
            throw;
        }
    }

    internal static void AssertColorRegion(SceneImage image, int left, int top, int width, int height,
        byte red, byte green, byte blue, int tolerance = 2, [CallerMemberName] string test = "")
    {
        try
        {
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int pixel = (y * image.Width + x) * 4;
                Assert.InRange(image.Pixels.Span[pixel], Math.Max(0, red - tolerance), Math.Min(255, red + tolerance));
                Assert.InRange(image.Pixels.Span[pixel + 1], Math.Max(0, green - tolerance), Math.Min(255, green + tolerance));
                Assert.InRange(image.Pixels.Span[pixel + 2], Math.Max(0, blue - tolerance), Math.Min(255, blue + tolerance));
                Assert.Equal((byte)255, image.Pixels.Span[pixel + 3]);
            }
        }
        catch (XunitException)
        {
            byte[] pixels = image.Pixels.ToArray();
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int pixel = (y * image.Width + x) * 4;
                pixels[pixel] = red;
                pixels[pixel + 1] = green;
                pixels[pixel + 2] = blue;
                pixels[pixel + 3] = 255;
            }
            SaveFailure(image with { Pixels = pixels }, image, test);
            throw;
        }
    }

    private static void SaveFailure(SceneImage expected, SceneImage actual, string test)
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("SOMEENGINE_TEST_ARTIFACTS") ??
            Path.Combine("artifacts", "test-results"), "images", $"{test}-frame-{actual.Frame}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        SavePng(Path.Combine(directory, "actual.png"), actual);
        SavePng(Path.Combine(directory, "expected.png"), expected);
        int width = Math.Max(expected.Width, actual.Width);
        int height = Math.Max(expected.Height, actual.Height);
        byte[] difference = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = (y * width + x) * 4;
            difference[pixel + 3] = 255;
            if (x >= expected.Width || y >= expected.Height || x >= actual.Width || y >= actual.Height)
            {
                difference[pixel] = 255;
                difference[pixel + 2] = 255;
                continue;
            }
            int expectedPixel = (y * expected.Width + x) * 4;
            int actualPixel = (y * actual.Width + x) * 4;
            int alphaError = Math.Abs(expected.Pixels.Span[expectedPixel + 3] - actual.Pixels.Span[actualPixel + 3]);
            for (int channel = 0; channel < 3; channel++)
                difference[pixel + channel] = (byte)Math.Max(alphaError,
                    Math.Abs(expected.Pixels.Span[expectedPixel + channel] - actual.Pixels.Span[actualPixel + channel]));
        }
        SavePng(Path.Combine(directory, "diff.png"), new(actual.Frame, width, height, difference));
        Console.Error.WriteLine($"Image assertion artifacts: {Path.GetFullPath(directory)}");
    }

    private static void SavePng(string path, SceneImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels.ToArray(), 0, bitmap.GetPixels(), image.Pixels.Length);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        encoded.SaveTo(file);
    }
}

namespace SomeEngine.Runtime.Tests;

internal static class SceneImages
{
    internal static async Task RunAsync(string backend, SceneScript script, params string[] options)
    {
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, .. options], script);
        Assert.True(result.Succeeded, $"{backend}: {result.Failure}, completed frames={result.CompletedFrames}; {result.Error}");
    }

    internal static void AssertEqual(SceneImage expected, SceneImage actual, int tolerance = 0)
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

    internal static void AssertEqualRegion(SceneImage expected, SceneImage actual, int left, int top, int width, int height)
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

    internal static void AssertColorRegion(SceneImage image, int left, int top, int width, int height,
        byte red, byte green, byte blue, int tolerance = 2)
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
}

using SkiaSharp;

namespace SomeEngine.Runtime.Tests;

public sealed class SceneImageTests
{
    [Fact]
    public void SavedBitmapKeepsImageDimensionsColorsAndTopDownRows()
    {
        var image = new SceneImage(4, 3, 2, new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
            255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 0, 255,
        });
        string path = Path.Combine(Path.GetTempPath(), $"SomeEngine-Image-{Guid.NewGuid():N}.bmp");
        try
        {
            image.SaveBmp(path);

            using SKBitmap bitmap = SKBitmap.Decode(path);
            Assert.Equal(3, bitmap.Width);
            Assert.Equal(2, bitmap.Height);
            Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(1, 0));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(2, 0));
            Assert.Equal(SKColors.White, bitmap.GetPixel(0, 1));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Yellow, bitmap.GetPixel(2, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

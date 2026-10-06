using System.Runtime.InteropServices;
using SkiaSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Tests;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class ImageArtifactTests
{
    [Fact]
    public void HalfColorAndDepthEvidenceKeepsRawValuesAndFullDifferentDimensions()
    {
        byte[] hdr = MemoryMarshal.AsBytes<Half>(new Half[] { (Half)4, (Half)0, (Half)0, (Half)1 }).ToArray();
        byte[] depth = MemoryMarshal.AsBytes<float>(new float[] { .25f, .75f }).ToArray();
        string directory = ImageArtifacts.SaveComparison(new(1, 1, Format.R16G16B16A16Float, hdr),
            new(2, 1, Format.D32Float, depth), nameof(HalfColorAndDepthEvidenceKeepsRawValuesAndFullDifferentDimensions));
        try
        {
            Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(directory, "expected.raw")));
            Assert.Equal(depth, File.ReadAllBytes(Path.Combine(directory, "actual.raw")));
            using var expected = SKBitmap.Decode(Path.Combine(directory, "expected.png"));
            using var actual = SKBitmap.Decode(Path.Combine(directory, "actual.png"));
            using var difference = SKBitmap.Decode(Path.Combine(directory, "diff.png"));
            Assert.Equal(1, expected.Width);
            Assert.Equal(2, actual.Width);
            Assert.Equal(2, difference.Width);
            Assert.Equal(new SKColor(255, 0, 255), difference.GetPixel(1, 0));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ShadowFailureKeepsImagesUnderTheDocumentedRootButDeletesWorkingContent()
    {
        var evidence = new ShadowEvidence("device-free-shadow-failure");
        string retained = Path.Combine(ImageArtifacts.Root, "images", Path.GetFileName(evidence.Directory));
        Directory.CreateDirectory(evidence.Content);
        File.WriteAllText(Path.Combine(evidence.Content, "working.asset"), "scratch");
        ShadowImageFiles.WriteGrayscale(Path.Combine(evidence.Directory, "rt-shadow.bmp"), 1, 1, [255]);
        ShadowImageFiles.WriteGrayscale(Path.Combine(evidence.Directory, "rendered-shadow.bmp"), 1, 1, [0]);
        ShadowImageFiles.WriteGrayscale(Path.Combine(evidence.Directory, "shadow-error.bmp"), 1, 1, [255]);
        evidence.Dispose();
        try
        {
            Assert.False(Directory.Exists(evidence.Content));
            Assert.True(File.Exists(Path.Combine(retained, "actual.png")));
            Assert.True(File.Exists(Path.Combine(retained, "expected.png")));
            Assert.True(File.Exists(Path.Combine(retained, "diff.png")));
            Assert.False(Directory.Exists(Path.Combine(retained, "content")));
        }
        finally { Directory.Delete(retained, true); }
    }
}

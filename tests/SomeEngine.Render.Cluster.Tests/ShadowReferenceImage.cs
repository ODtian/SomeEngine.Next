using System.Buffers.Binary;
using System.Numerics;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ShadowReferenceImage
{
    internal static ShadowReferenceComparison Compare(string directory, ShadowReferenceRays rays, int width, int height,
        int edgeExclusionPixels = 2)
    {
        float[] shadowed = ReadLinearColor(Path.Combine(directory, "shadowed.bmp"), width, height);
        float[] lit = ReadLinearColor(Path.Combine(directory, "unshadowed.bmp"), width, height);
        float[] ambient = ReadLinearColor(Path.Combine(directory, "ambient.bmp"), width, height);
        var expected = new int[width * height];
        var positions = new Vector3[expected.Length];
        var normals = new Vector3[expected.Length];
        var observed = new float[expected.Length];
        Array.Fill(expected, -1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x;
                if (!rays.ReceiverAt(x, y, out Vector3 position, out Vector3 normal)) continue;
                Vector3 toLight = rays.ToLight(position);
                if (Vector3.Dot(normal, Vector3.Normalize(toLight)) < .15f) continue;
                if (lit[pixel] - ambient[pixel] < .03f) continue;
                positions[pixel] = position;
                normals[pixel] = normal;
                expected[pixel] = rays.Occluded(position, normal) ? 0 : 1;
                observed[pixel] = (shadowed[pixel] - ambient[pixel]) / (lit[pixel] - ambient[pixel]);
            }

        int darkCount = 0, litCount = 0, mismatched = 0, missed = 0, falseShadows = 0;
        float sumError = 0, maxError = 0;
        var difference = new byte[expected.Length];
        using var csv = new StreamWriter(Path.Combine(directory, "receiver-comparison.csv"));
        csv.WriteLine("x,y,worldX,worldY,worldZ,normalX,normalY,normalZ,referenceVisibility,renderedVisibility,absoluteError");
        for (int y = edgeExclusionPixels; y < height - edgeExclusionPixels; y++)
            for (int x = edgeExclusionPixels; x < width - edgeExclusionPixels; x++)
            {
                int pixel = y * width + x, visibility = expected[pixel];
                if (visibility < 0) continue;
                bool interior = true;
                for (int dy = -edgeExclusionPixels; dy <= edgeExclusionPixels; dy++)
                    for (int dx = -edgeExclusionPixels; dx <= edgeExclusionPixels; dx++)
                        interior &= expected[pixel + dy * width + dx] == visibility;
                if (!interior) continue;
                if (visibility == 0) darkCount++;
                else litCount++;
                float error = MathF.Abs(observed[pixel] - visibility);
                sumError += error;
                maxError = Math.Max(maxError, error);
                if (error > .15f)
                {
                    mismatched++;
                    if (visibility == 0) missed++;
                    else falseShadows++;
                    difference[pixel] = 255;
                }
                Vector3 position = positions[pixel];
                Vector3 normal = normals[pixel];
                csv.WriteLine(FormattableString.Invariant(
                    $"{x},{y},{position.X:R},{position.Y:R},{position.Z:R},{normal.X:R},{normal.Y:R},{normal.Z:R},{visibility},{observed[pixel]:R},{error:R}"));
            }
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "triangle-reference.bmp"), width, height,
            expected.Select(value => value < 0 ? (byte)127 : (byte)(value * 255)).ToArray());
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "rendered-visibility.bmp"), width, height,
            observed.Select(value => (byte)Math.Clamp(value * 255, 0, 255)).ToArray());
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "visibility-error.bmp"), width, height, difference);
        int compared = darkCount + litCount;
        return new(darkCount, litCount, mismatched, compared, compared == 0 ? 1 : (float)mismatched / compared,
            compared == 0 ? 1 : sumError / compared, maxError, missed, falseShadows);
    }

    private static float[] ReadLinearColor(string path, int expectedWidth, int expectedHeight)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int data = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10));
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22));
        int height = Math.Abs(signedHeight);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) / 8;
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Contains(channels, new[] { 3, 4 });
        var pixels = new float[width * height];
        int rowPitch = (width * channels + 3) & ~3;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = data + (signedHeight < 0 ? y : height - 1 - y) * rowPitch + x * channels;
                float color = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    float mapped = MathF.Pow(bytes[offset + channel] / 255f, 2.2f);
                    color += mapped / Math.Max(1 - mapped, 1e-6f);
                }
                pixels[y * width + x] = color / 3;
            }
        return pixels;
    }

    private static void WriteBitmap(string path, int width, int height, byte[] pixels)
    {
        const int header = 54;
        var bytes = new byte[header + width * height * 4];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
        for (int pixel = 0; pixel < pixels.Length; pixel++)
        {
            int offset = header + pixel * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = pixels[pixel];
            bytes[offset + 3] = 255;
        }
        File.WriteAllBytes(path, bytes);
    }
}

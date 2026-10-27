using System.Numerics;

namespace SomeEngine.Render.Cluster.Tests;

internal sealed record RayShadowSoftComparison(int ComparedPixels, int ShadowedPixels, int LitPixels,
    int PenumbraPixels, int MismatchedPixels, float MismatchFraction, float MeanVisibilityError,
    float MaximumVisibilityError, float FalseShadowFraction, float MissedShadowFraction,
    int MissingRenderedReceiverPixels, float RenderedReceiverCoverage, int SelfHitPixels,
    int SkyPixels,
    int GeometryFacingLightRejectedPixels, int RangeRejectedPixels, int ConeRejectedPixels,
    int ReferenceShadingRejectedPixels, int RenderedShadingRejectedPixels)
{
    internal static RayShadowSoftComparison Write(string directory, RayShadowImage.SoftPixel[] reference,
        int width, int height, Vector3[]? linearVisibility = null)
    {
        Vector3[] actual = linearVisibility ?? ShadowImageFiles.ReadRawLinearPixels(
            Path.Combine(directory, "vsm-shadow.rgba16"), width, height);
        int compared = 0, shadowed = 0, lit = 0, penumbra = 0, mismatches = 0, missing = 0, selfHits = 0;
        int sky = 0, geometryRejected = 0, rangeRejected = 0, coneRejected = 0;
        int referenceShadingRejected = 0, renderedShadingRejected = 0;
        double errorSum = 0, falseShadowSum = 0, missedShadowSum = 0, litAmount = 0, shadowAmount = 0;
        float maximumError = 0;
        var expectedImage = new byte[reference.Length];
        var actualImage = new byte[reference.Length];
        var errors = new byte[reference.Length];
        var coverage = new byte[reference.Length];
        using var csv = new StreamWriter(Path.Combine(directory, "ray-comparison.csv"));
        csv.WriteLine("x,y,referenceVisibility,renderedVisibility,receiverCoverage,referenceShadingRejected,renderedShadingRejected,selfHit,absoluteVisibilityError,rangeRejected,coneRejected,rawVisibilityNumerator,rawShadingNumerator");
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x;
            uint flags = reference[pixel].Flags;
            bool skyPixel = flags == uint.MaxValue;
            bool eligible = !skyPixel && (flags & 3u) <= 1u;
            bool rendered = float.IsFinite(actual[pixel].Y) && actual[pixel].Y > .001f;
            float observed = actual[pixel].X / actual[pixel].Y;
            bool finite = float.IsFinite(actual[pixel].X) && float.IsFinite(actual[pixel].Y) &&
                float.IsFinite(actual[pixel].Z) && float.IsFinite(observed);
            float expected = reference[pixel].Visibility;
            bool referenceRejected = !skyPixel && (flags & 4u) != 0;
            bool renderedRejected = rendered && actual[pixel].Z / actual[pixel].Y < .5f;
            bool selfHit = !skyPixel && (flags & 8u) != 0;
            bool outsideRange = !skyPixel && (flags & 16u) != 0;
            bool outsideCone = !skyPixel && (flags & 32u) != 0;
            float error = eligible ? rendered && finite ? MathF.Abs(observed - expected) : 1 : 0;
            if (skyPixel) sky++;
            else if (outsideRange) rangeRejected++;
            else if (outsideCone) coneRejected++;
            else if (!eligible) geometryRejected++;
            if (eligible)
            {
                compared++;
                if (expected < .5f) shadowed++; else lit++;
                if (expected > 0 && expected < 1) penumbra++;
                if (!rendered) missing++;
                if (selfHit) selfHits++;
                if (referenceRejected) referenceShadingRejected++;
                if (renderedRejected) renderedShadingRejected++;
                if (error > .15f) mismatches++;
                errorSum += error;
                maximumError = Math.Max(maximumError, error);
                litAmount += expected;
                shadowAmount += 1 - expected;
                falseShadowSum += rendered && finite ? Math.Max(expected - observed, 0) : expected;
                missedShadowSum += rendered && finite ? Math.Max(observed - expected, 0) : 1 - expected;
            }
            expectedImage[pixel] = eligible ? Preview(expected) : (byte)127;
            actualImage[pixel] = Preview(observed);
            errors[pixel] = Preview(error);
            coverage[pixel] = Preview(actual[pixel].Y);
            csv.WriteLine(FormattableString.Invariant(
                $"{x},{y},{(eligible ? expected : -1):R},{observed:R},{actual[pixel].Y:R},{(referenceRejected ? 1 : 0)},{(renderedRejected ? 1 : 0)},{(selfHit ? 1 : 0)},{error:R},{(outsideRange ? 1 : 0)},{(outsideCone ? 1 : 0)},{actual[pixel].X:R},{actual[pixel].Z:R}"));
        }
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "rt-shadow.bmp"), width, height, expectedImage);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "rendered-shadow.bmp"), width, height, actualImage);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "shadow-error.bmp"), width, height, errors);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "receiver-coverage.bmp"), width, height, coverage);
        return new(compared, shadowed, lit, penumbra, mismatches,
            compared == 0 ? 0 : (float)mismatches / compared,
            compared == 0 ? 0 : (float)(errorSum / compared), maximumError,
            litAmount == 0 ? 0 : (float)(falseShadowSum / litAmount),
            shadowAmount == 0 ? 0 : (float)(missedShadowSum / shadowAmount),
            missing, compared == 0 ? 0 : (float)(compared - missing) / compared, selfHits,
            sky, geometryRejected, rangeRejected, coneRejected, referenceShadingRejected, renderedShadingRejected);
    }

    private static byte Preview(float value) => float.IsFinite(value)
        ? (byte)Math.Clamp(value * 255, 0, 255) : (byte)127;
}

namespace SomeEngine.Render.Cluster.Tests;

internal sealed record RayShadowComparison(
    ShadowReferenceComparison AllReceivers, ShadowReferenceComparison Interior,
    ShadowReferenceComparison Boundary, int ReferenceReceiverPixels, int SkyPixels,
    int GeometryFacingLightRejectedPixels, int MissingRenderedReceiverPixels,
    int ReferenceShadingRejectedPixels, int RenderedShadingRejectedPixels, int SelfHitPixels,
    float RenderedReceiverCoverage)
{
    internal static RayShadowComparison Write(string directory, uint[] reference, int width, int height)
    {
        var actual = ShadowImageFiles.ReadLinearPixels(Path.Combine(directory, "vsm-shadow.bmp"), width, height);
        var all = new VisibilityErrors();
        var interior = new VisibilityErrors();
        var boundary = new VisibilityErrors();
        int receivers = 0, sky = 0, geometryRejected = 0, missing = 0;
        int referenceShadingRejected = 0, renderedShadingRejected = 0, selfHits = 0;
        var expectedImage = new byte[reference.Length];
        var errors = new byte[reference.Length];
        var coverage = new byte[reference.Length];
        var shading = new byte[reference.Length];
        using var csv = new StreamWriter(Path.Combine(directory, "ray-comparison.csv"));
        csv.WriteLine("x,y,referenceVisibility,renderedVisibility,receiverCoverage,referenceShadingRejected,renderedShadingRejected,selfHit,boundary,absoluteVisibilityError");
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x;
            uint expected = reference[pixel] & 3u;
            bool skyPixel = reference[pixel] == uint.MaxValue;
            bool eligible = expected <= 1 && !skyPixel;
            bool rendered = actual[pixel].Y > .001f;
            float observed = rendered ? Math.Clamp(actual[pixel].X / actual[pixel].Y, 0, 1) : 0;
            bool referenceRejected = !skyPixel && (reference[pixel] & 4u) != 0;
            bool renderedRejected = rendered && actual[pixel].Z / actual[pixel].Y < .5f;
            bool selfHit = !skyPixel && (reference[pixel] & 8u) != 0;
            bool edge = eligible && IsBoundary(reference, x, y, width, height);
            float error = eligible ? rendered ? MathF.Abs(observed - expected) : 1 : 0;
            if (skyPixel) sky++;
            else if (!eligible) geometryRejected++;
            if (eligible)
            {
                receivers++;
                if (!rendered) missing++;
                if (referenceRejected) referenceShadingRejected++;
                if (renderedRejected) renderedShadingRejected++;
                if (selfHit) selfHits++;
                all.Add(expected, error, rendered);
                (edge ? boundary : interior).Add(expected, error, rendered);
            }
            expectedImage[pixel] = eligible ? (byte)(expected * 255) : (byte)127;
            errors[pixel] = (byte)Math.Clamp(error * 255, 0, 255);
            coverage[pixel] = (byte)Math.Clamp(actual[pixel].Y * 255, 0, 255);
            shading[pixel] = referenceRejected ? (byte)255 : (byte)0;
            csv.WriteLine(FormattableString.Invariant(
                $"{x},{y},{(eligible ? (int)expected : -1)},{observed:R},{actual[pixel].Y:R},{(referenceRejected ? 1 : 0)},{(renderedRejected ? 1 : 0)},{(selfHit ? 1 : 0)},{(edge ? 1 : 0)},{error:R}"));
        }
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "rt-shadow.bmp"), width, height, expectedImage);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "shadow-error.bmp"), width, height, errors);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "receiver-coverage.bmp"), width, height, coverage);
        ShadowImageFiles.WriteGrayscale(Path.Combine(directory, "shading-rejection.bmp"), width, height, shading);
        return new(all.Result(), interior.Result(), boundary.Result(), receivers, sky, geometryRejected,
            missing, referenceShadingRejected, renderedShadingRejected, selfHits,
            receivers == 0 ? 0 : (float)(receivers - missing) / receivers);
    }

    private static bool IsBoundary(uint[] reference, int x, int y, int width, int height)
    {
        uint expected = reference[y * width + x] & 3u;
        if (x < 2 || y < 2 || x >= width - 2 || y >= height - 2) return true;
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
            if ((reference[(y + dy) * width + x + dx] & 3u) != expected) return true;
        return false;
    }

    private sealed class VisibilityErrors
    {
        private int _shadowed, _lit, _errors, _missed, _falseShadows;
        private double _sum;
        private float _maximum;

        internal void Add(uint expected, float error, bool rendered)
        {
            if (expected == 0) _shadowed++;
            else _lit++;
            _sum += error;
            _maximum = Math.Max(_maximum, error);
            if (error <= .15f) return;
            _errors++;
            if (!rendered) return;
            if (expected == 0) _missed++;
            else _falseShadows++;
        }

        internal ShadowReferenceComparison Result()
        {
            int count = _shadowed + _lit;
            return new(_shadowed, _lit, _errors, count, count == 0 ? 0 : (float)_errors / count,
                count == 0 ? 0 : (float)(_sum / count), _maximum, _missed, _falseShadows);
        }
    }
}

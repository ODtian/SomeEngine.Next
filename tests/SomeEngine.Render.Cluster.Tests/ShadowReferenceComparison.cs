namespace SomeEngine.Render.Cluster.Tests;

internal sealed record ShadowReferenceComparison(int ShadowedPixels, int LitPixels, int MismatchedPixels,
    int ComparedPixels, float MismatchFraction, float MeanVisibilityError, float MaximumVisibilityError,
    int MissedShadowPixels, int FalseShadowPixels);

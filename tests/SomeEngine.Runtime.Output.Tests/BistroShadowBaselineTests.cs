using System.Numerics;
using SomeEngine.Runtime;
using SomeEngine.Render.Cluster.Tests;
using Xunit;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class BistroShadowBaselineTests
{
    private static BistroShadowBaseline ReadBaseline() => BistroShadowBaseline.Read(
        Path.Combine(AppContext.BaseDirectory, "Baselines", "bistro-hard-default.json"));

    [Fact]
    public void ReviewedNormalCapturesRemainInsideTheirMeasuredRegionalEnvelope()
    {
        BistroShadowBaseline baseline = ReadBaseline();
        Assert.Equal(3, baseline.Observations.Length);
        Assert.All(baseline.Observations, sample => Assert.True(baseline.Accepts(sample)));
    }

    [Fact]
    public void RecordedPreNativeDepthQualityRegressionIsRejected()
    {
        // Retained bistro-lod/sidedness-default comparison. This is a recorded
        // worse result, not a claim that every difference isolates one cause.
        var recorded = new ShadowAccuracyObservation(
            "sidedness-default", 795215, 37758, 32362, 5394, 2, 0.0474814987f,
            new(474362, 1788, 1716, 72, 0.0037692732554462627f),
            new(320853, 35970, 30646, 5322, 0.11210741367542146f));
        Assert.False(ReadBaseline().Accepts(recorded));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ChangedPopulationOrAnyWorsenedErrorChannelIsRejected(int change)
    {
        BistroShadowBaseline baseline = ReadBaseline();
        ShadowAccuracyObservation sample = baseline.Observations.MaxBy(value => value.MismatchedPixels)!;
        ShadowAccuracyObservation changed = change switch
        {
            0 => sample with { ComparedPixels = sample.ComparedPixels - 1 },
            1 => sample with { MismatchedPixels = sample.MismatchedPixels + 1 },
            2 => sample with { MissedShadowPixels = sample.MissedShadowPixels + 1 },
            3 => sample with { FalseShadowPixels = sample.FalseShadowPixels + 1 },
            4 => sample with { MissingRenderedReceiverPixels = sample.MissingRenderedReceiverPixels + 1 },
            5 => sample with { MeanVisibilityError = sample.MeanVisibilityError + 0.000001f },
            _ => sample with { MeanVisibilityError = float.NaN }
        };
        Assert.False(baseline.Accepts(changed));
    }

    [Fact]
    public void ImprovementInOneErrorClassCannotHideRegressionInAnother()
    {
        BistroShadowBaseline baseline = ReadBaseline();
        ShadowAccuracyObservation sample = baseline.Observations.MaxBy(value => value.MismatchedPixels)!;
        Assert.False(baseline.Accepts(sample with
        {
            MissedShadowPixels = sample.MissedShadowPixels - 1,
            FalseShadowPixels = sample.FalseShadowPixels + 1
        }));
        Assert.False(baseline.Accepts(sample with
        {
            MissedShadowPixels = sample.MissedShadowPixels + 1,
            FalseShadowPixels = sample.FalseShadowPixels - 1
        }));
    }
    [Fact]
    public void CatastrophicAllLitAllShadowAndMissingOutputsAreRejected()
    {
        BistroShadowBaseline baseline = ReadBaseline();
        ShadowAccuracyObservation sample = baseline.Observations.MaxBy(value => value.MismatchedPixels)!;
        // Worst-case policy counterexamples, not additional Bistro captures.
        int population = sample.ComparedPixels;
        Assert.False(baseline.Accepts(sample with
        {
            MismatchedPixels = population,
            MissedShadowPixels = population,
            FalseShadowPixels = 0,
            MeanVisibilityError = 1
        }));
        Assert.False(baseline.Accepts(sample with
        {
            MismatchedPixels = population,
            MissedShadowPixels = 0,
            FalseShadowPixels = population,
            MeanVisibilityError = 1
        }));
        Assert.False(baseline.Accepts(sample with { MissingRenderedReceiverPixels = population }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void BaselineCannotCarryAcrossChangedPublicSceneInputs(int change)
    {
        var mesh = new SceneMeshTransform(Guid.Parse("12345678-1234-5678-9abc-123456789abc"), Matrix4x4.Identity);
        var frame = new SceneFrame { Width = 128, Height = 128, View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity, Meshes = [mesh] };
        var baseline = new BistroShadowBaseline
        {
            SourceGeometrySha256 = "unused-in-frame-check",
            FrameContract = BistroShadowBaseline.DescribeFrame(frame),
            Observations = ReadBaseline().Observations
        };
        Assert.True(baseline.MatchesFrame(frame));
        SceneFrame changed = change switch
        {
            0 => frame with { Width = 256 },
            1 => frame with { Height = 256 },
            2 => frame with { CameraPosition = Vector3.UnitX },
            3 => frame with { View = Matrix4x4.CreateTranslation(1, 0, 0) },
            4 => frame with { Projection = Matrix4x4.CreateScale(2) },
            5 => frame with { Meshes = [mesh with { Transform = Matrix4x4.CreateTranslation(1, 0, 0) }] },
            6 => frame with { Meshes = [mesh with { MeshAsset = Guid.Empty }] },
            _ => frame with { Options = RuntimeStartupOptions.Parse(["--graphics-backend", "vulkan"]) }
        };
        Assert.False(baseline.MatchesFrame(changed));
    }

    [Fact]
    public void ReviewedBoundaryAllowanceCannotHideAnInteriorOrRegionalRegression()
    {
        BistroShadowBaseline baseline = ReadBaseline();
        ShadowAccuracyObservation sample = baseline.Observations.MaxBy(value => value.MismatchedPixels)!;
        ShadowAccuracyRegion interior = sample.Interior!;
        ShadowAccuracyRegion boundary = sample.Boundary!;
        // Same aggregate totals: move the one reviewed boundary miss into the interior.
        Assert.False(baseline.Accepts(sample with
        {
            Interior = interior with { MismatchedPixels = interior.MismatchedPixels + 1,
                MissedShadowPixels = interior.MissedShadowPixels + 1 },
            Boundary = boundary with { MismatchedPixels = boundary.MismatchedPixels - 1,
                MissedShadowPixels = boundary.MissedShadowPixels - 1 }
        }));
        foreach (bool changeInterior in new[] { true, false })
        {
            ShadowAccuracyRegion region = changeInterior ? interior : boundary;
            ShadowAccuracyRegion[] changes =
            [
                region with { ComparedPixels = region.ComparedPixels - 1 },
                region with { MismatchedPixels = region.MismatchedPixels + 1 },
                region with { MissedShadowPixels = region.MissedShadowPixels + 1 },
                region with { FalseShadowPixels = region.FalseShadowPixels + 1 },
                region with { MeanVisibilityError = region.MeanVisibilityError + 0.000001f },
                region with { MeanVisibilityError = float.NaN },
            ];
            foreach (ShadowAccuracyRegion changed in changes)
                Assert.False(baseline.Accepts(changeInterior
                    ? sample with { Interior = changed } : sample with { Boundary = changed }));
        }
        Assert.False(baseline.Accepts(sample with { Interior = null }));
        Assert.False(baseline.Accepts(sample with { Boundary = null }));
    }

    [Theory]
    [InlineData(20000, .02f, true)]
    [InlineData(20001, .019f, false)]
    [InlineData(19999, .020001f, false)]
    [InlineData(45970, .04597f, false)]
    public void UnprofiledConfigurationsRetainBothOriginalTwoPercentLimits(
        int mismatches, float meanError, bool accepted)
    {
        var observation = new ShadowAccuracyObservation("unprofiled", 1000000,
            mismatches, mismatches, 0, 0, meanError);
        Assert.Equal(accepted, BistroShadowBaseline.AcceptsUnprofiled(observation));
    }

}

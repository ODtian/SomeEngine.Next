using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPipelineOutputTests
{
    [Fact]
    public void Output_description_requires_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(1, 1, 0));

        var description = new ClusterPipelineOutputDescription(64, 32, 4);
        Assert.Equal(64, description.Width);
        Assert.Equal(32, description.Height);
        Assert.Equal(4u, description.ViewCapacity);
    }

    [Fact]
    public void Geometry_outputs_are_explicit_and_have_no_presentation_target()
    {
        var outputs = new ClusterPipelineOutputs(
            default,
            default,
            default,
            null!,
            64,
            32,
            4);

        Assert.False(outputs.IsValid);
        Assert.Equal(64, outputs.Width);
        Assert.Equal(32, outputs.Height);
        Assert.Equal(4u, outputs.ViewCapacity);
        Assert.DoesNotContain(
            typeof(ClusterPipelineOutputs).GetProperties(),
            static property => property.Name.Contains(
                "Present",
                StringComparison.OrdinalIgnoreCase));
    }
}

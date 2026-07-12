using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class ResourceClearTests
{
    [Fact]
    public void Null_clear_buffer_color_depth_and_stencil_respects_ranges()
    {
        PortableClearScenarios scenarios = new();
        scenarios.Null_clears_exact_buffer_range_with_repeating_uint_pattern();
        scenarios.Null_color_and_depth_stencil_clears_write_only_selected_aspects();
    }
}

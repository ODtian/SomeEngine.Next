using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Tests;

public sealed class ScalarLayoutTests
{
    [Fact]
    public void FromFieldsDropsDuplicateAndOutOfRangeFields()
    {
        ScalarLayout layout = ScalarLayout.FromFields(
            "TestScalars",
            [
                new ScalarFieldLayout("BaseColor", 0, 16, 1, 4, 8),
                new ScalarFieldLayout("BaseColor", 16, 4, 1, 1, 8),
                new ScalarFieldLayout("TooLarge", 32, 16, 1, 4, 8),
            ],
            16);

        Assert.Equal(ScalarLayout.HeaderByteSize + ScalarLayout.PayloadAlignment, layout.ByteSize);
        Assert.Equal("TestScalars", layout.Name);
        Assert.Single(layout.Fields);
        Assert.Equal("BaseColor", layout.Fields[0].Name);
    }
}

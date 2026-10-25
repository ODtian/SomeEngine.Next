using System.Text.Json;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests;

public sealed class ShaderBuildMeasurementsTests
{
    [Fact]
    public void Missing_output_writer_is_rejected()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => ShaderBuildMeasurements.Start(null!, null));
        Assert.Equal("output", error.ParamName);
    }

    [Fact]
    public void Empty_measurement_path_reports_only_to_the_supplied_writer()
    {
        using var output = new StringWriter();
        using (ShaderBuildMeasurements.Start(output, "")) { }

        string line = Assert.Single(output.ToString().Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Shader compilations: ", line);
    }

    [Fact]
    public void Dash_measurement_path_writes_json_to_the_supplied_writer()
    {
        using var output = new StringWriter();
        using (ShaderBuildMeasurements.Start(output, "-")) { }

        using var lines = new StringReader(output.ToString());
        Assert.StartsWith("Shader compilations: ", lines.ReadLine()!);
        using JsonDocument measurements = JsonDocument.Parse(lines.ReadToEnd());
        Assert.Equal(JsonValueKind.Array, measurements.RootElement.ValueKind);
    }

    [Fact]
    public void Explicit_measurement_path_writes_a_json_file()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var output = new StringWriter();
            using (ShaderBuildMeasurements.Start(output, path)) { }

            using JsonDocument measurements = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(JsonValueKind.Array, measurements.RootElement.ValueKind);
            string line = Assert.Single(output.ToString().Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries));
            Assert.StartsWith("Shader compilations: ", line);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Disposing_twice_does_not_repeat_the_measurement_output()
    {
        using var output = new StringWriter();
        using var measurements = ShaderBuildMeasurements.Start(output, "-");
        measurements.Dispose();
        string first = output.ToString();

        measurements.Dispose();

        Assert.NotEmpty(first);
        Assert.Equal(first, output.ToString());
    }
}

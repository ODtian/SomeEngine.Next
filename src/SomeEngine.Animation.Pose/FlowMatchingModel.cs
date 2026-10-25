using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation.Pose;

[JsonConverter(typeof(JsonStringEnumConverter<FlowControlKind>))]
public enum FlowControlKind : byte
{
    [JsonStringEnumMemberName("vector3")] Vector3,
    [JsonStringEnumMemberName("rotation6d")] Rotation6D,
}

/// <summary>One ordered typed field in the model's product control representation.</summary>
public readonly record struct FlowControlField(string Name, FlowControlKind Kind, int Count)
{
    [JsonIgnore] public int Offset { get; internal init; }
    [JsonIgnore] public int Width => Count * (Kind == FlowControlKind.Vector3 ? 3 : 6);

    public void Encode(Vector3 value, Span<float> product, int element = 0)
    {
        int first = Offset + element * 3;
        product[first] = value.X;
        product[first + 1] = value.Y;
        product[first + 2] = value.Z;
    }

    public void Encode(Quaternion rotation, Span<float> product, int element = 0)
    {
        Matrix4x4 axes = Matrix4x4.CreateFromQuaternion(rotation);
        int first = Offset + element * 6;
        product[first] = axes.M11;
        product[first + 1] = axes.M21;
        product[first + 2] = axes.M12;
        product[first + 3] = axes.M22;
        product[first + 4] = axes.M13;
        product[first + 5] = axes.M23;
    }
}

/// <summary>Ready immutable parameters for one conditional flow and its learned ELU pose decoder.</summary>
public sealed class FlowMatchingModel
{
    private readonly FlowMatchingData _data;
    public int BoneCount => _data.BoneCount;
    public int LatentCount => _data.InitialHistory.Length;
    public int ControlCount => _data.ControlOffset.Length;
    public int IntegrationSteps => _data.Steps;
    public float FrameTime => _data.FrameTime;
    public ReadOnlySpan<FlowControlField> Controls => _data.Controls;
    public ReadOnlySpan<float> InitialHistory => _data.InitialHistory;
    internal FlowMatchingData Data => _data;

    private FlowMatchingModel(FlowMatchingData data)
    {
        DevelopmentCheck.Require(data.Version == 1, "Flow Matching model requires export version 1.");
        _data = data;
        int offset = 0;
        for (int field = 0; field < data.Controls.Length; field++)
        {
            data.Controls[field] = data.Controls[field] with { Offset = offset };
            offset += data.Controls[field].Width;
        }
    }

    public static FlowMatchingModel ReadJson(ReadOnlySpan<byte> json) =>
        new(JsonSerializer.Deserialize(json, FlowMatchingJson.Default.FlowMatchingData)!);

    internal static FlowMatchingModel ReadJson(string json) =>
        new(JsonSerializer.Deserialize(json, FlowMatchingJson.Default.FlowMatchingData)!);

    internal string WriteJson() => JsonSerializer.Serialize(_data, FlowMatchingJson.Default.FlowMatchingData);
}

internal sealed class FlowMatchingData
{
    public int Version { get; set; }
    public int BoneCount { get; set; }
    public float FrameTime { get; set; }
    public int Steps { get; set; }
    public FlowControlField[] Controls { get; set; } = [];
    public float[] ControlOffset { get; set; } = [];
    public float[] ControlScale { get; set; } = [];
    public float[] LatentOffset { get; set; } = [];
    public float[] LatentScale { get; set; } = [];
    public float[] LatentDeviation { get; set; } = [];
    public float[] LatentMinimum { get; set; } = [];
    public float[] LatentMaximum { get; set; } = [];
    public float[] InitialHistory { get; set; } = [];
    public float[] PoseOffset { get; set; } = [];
    public float[] PoseScale { get; set; } = [];
    public float[] PoseDeviation { get; set; } = [];
    public FlowNetworkData Flow { get; set; } = new();
    public FlowNetworkData Decoder { get; set; } = new();
}

internal sealed class FlowNetworkData
{
    public int[] Widths { get; set; } = [];
    public float[][] Weights { get; set; } = [];
    public float[][] Biases { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlowMatchingData))]
internal sealed partial class FlowMatchingJson : JsonSerializerContext;

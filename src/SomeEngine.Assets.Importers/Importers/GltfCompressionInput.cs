using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace SomeEngine.Assets.Importers;

/// <summary>Preserves source accessors for primitive importers to decode and check.</summary>
internal static class GltfCompressionInput
{
    public static ModelRoot Load(string path)
    {
        using var stream = File.OpenRead(path);
        bool binary = ReadContext.IdentifyBinaryContainer(stream);
        // Primitive importers consume expanded accessors. Library content checks assume ordinary indices,
        // two influence sets and an already decoded vertex buffer, and can repair source values.
        return ModelRoot.Load(path, new ReadSettings
        {
            Validation = ValidationMode.Skip,
            JsonPreprocessor = json => AllowCompression(json, binary),
        });
    }

    private static bool ContainsCompression(JsonNode document)
    {
        if (document["bufferViews"] is JsonArray views && views.Any(view =>
            view?["extensions"]?["EXT_meshopt_compression"] is not null || view?["extensions"]?["KHR_meshopt_compression"] is not null)) return true;
        return document["meshes"] is JsonArray meshes && meshes.Any(mesh => mesh?["primitives"] is JsonArray primitives &&
            primitives.Any(primitive => primitive?["extensions"]?["KHR_draco_mesh_compression"] is not null));
    }

    private static string AllowCompression(string json, bool binary)
    {
        JsonNode document = JsonNode.Parse(json)!;
        if (!ContainsCompression(document)) return json;
        if (document["extensionsRequired"] is JsonArray required)
        {
            for (int index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() is "EXT_meshopt_compression" or "KHR_meshopt_compression" or "KHR_draco_mesh_compression")
                    required.RemoveAt(index);
            if (required.Count == 0) document.AsObject().Remove("extensionsRequired");
        }
        if (document["buffers"] is JsonArray buffers)
            for (int index = 0; index < buffers.Count; index++)
                if (buffers[index]!["uri"] is null && (!binary || index > 0))
                {
                    int length = buffers[index]!["byteLength"]!.GetValue<int>();
                    buffers[index]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(new byte[length]);
                }
        return document.ToJsonString();
    }
}

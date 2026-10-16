using System.Text;
using System.Numerics;
using System.Globalization;
using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct TransparencyMember(Shader Shader, ClusterVisibilityDepth VisibilityDepth);
    private sealed record TransparencyPipeline(Shader Shader, ClusterComputePipeline Pipeline);

    private sealed class TransparencyShaders(TransparencyMember[] members, uint[] resourceCapacities,
        Dictionary<string, TransparencyPipeline> pipelines)
    {
        internal readonly TransparencyMember[] Members = members;
        internal readonly uint[] ResourceCapacities = resourceCapacities;
        internal readonly Dictionary<string, TransparencyPipeline> Pipelines = pipelines;
        internal readonly uint TriangleStride = GetTriangleStride(pipelines["BuildTransparentGeometry"].Shader);
        private static uint GetTriangleStride(Shader shader)
        {
            ShaderReflection reflection = shader.GetSlangReflection(0);
            TypeReflection type = reflection.FindTypeByName("TransparencyTriangle")!.Value;
            return checked((uint)reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer)!.Value.GetSize(SlangParameterCategory.Uniform));
        }
    }

    private readonly List<TransparencyShaders> _transparencyShaders = [];

    private TransparencyShaders GetTransparencyShaders(ClusterShadeInvocation[] materials, out ClusterShadeInvocation[][] resources)
    {
        TransparencyMember[] members = materials.Select(stage =>
            new TransparencyMember(stage.Material.Shader!, stage.VisibilityDepth)).Distinct()
            .OrderBy(member => member.Shader.GetSlangSourceUnitName(member.Shader.SlangSourceUnitCount - 1), StringComparer.Ordinal)
            .ThenBy(member => member.VisibilityDepth).ToArray();
        foreach (TransparencyShaders existing in _transparencyShaders)
        {
            if (existing.Members.Length != members.Length || !existing.Members.All(members.Contains)) continue;
            resources = GroupResources(existing.Members);
            if (resources.Select((group, index) => (uint)group.Length <= existing.ResourceCapacities[index]).All(value => value))
                return existing;
        }

        resources = GroupResources(members);
        uint[] capacities = resources.Select(group => BitOperations.RoundUpToPowerOf2((uint)group.Length)).ToArray();
        Shader source = members[0].Shader;
        string[] arguments = [((uint)members[0].VisibilityDepth).ToString(CultureInfo.InvariantCulture),
            source.TryGetSlangEntry(0, ClusterMaterialEntries.TessellationSeed, SlangStage.Compute, out _) ? "true" : "false"];
        bool saved = members.Length == 1 && capacities[0] == 1 &&
            source.TryGetSpecialization("ResolveTransparentColor", arguments, out _);
        Shader? composition = saved ? null : ComposeTransparencyShaders(members, capacities, _device.Capabilities.CompileTarget);
        var pipelines = new Dictionary<string, TransparencyPipeline>();
        foreach (string entry in new[] { "ClearTransparentTiles", "CopyTransparentSources", "BuildTransparentGeometry", "ReserveTransparentCells",
                     "ScatterTransparentCells", "RequestTransparentShadows", "ResolveTransparentColor" })
        {
            Shader shader = composition ?? (entry is "BuildTransparentGeometry" or "RequestTransparentShadows" or "ResolveTransparentColor"
                ? source.RequireSpecialization(entry, arguments) : source);
            pipelines.Add(entry, new(shader, ClusterComputePipeline.Create(_backend, _device, shader, entry, entry)));
        }
        var result = new TransparencyShaders(members, capacities, pipelines);
        _transparencyShaders.Add(result);
        return result;

        ClusterShadeInvocation[][] GroupResources(TransparencyMember[] types) => types.Select(member => materials
            .Where(stage => ReferenceEquals(stage.Material.Shader, member.Shader) && stage.VisibilityDepth == member.VisibilityDepth)
            .DistinctBy(stage => stage.CommandBindings).ToArray()).ToArray();
    }

    private static Shader ComposeTransparencyShaders(TransparencyMember[] materials, uint[] resourceCapacities, SlangCompileTarget target)
        => materials[0].Shader.Compose("Cluster transparent materials", "scene_transparency.slang",
            CreateTransparencySource(materials, resourceCapacities), materials.Skip(1).Select(stage => stage.Shader).ToArray(), target);

    private static string CreateTransparencySource(TransparencyMember[] materials, uint[] resourceCapacities)
    {
        var source = new StringBuilder();
        foreach (string unit in materials.SelectMany(stage => Enumerable.Range(0, stage.Shader.SlangSourceUnitCount)
                     .Select(stage.Shader.GetSlangSourceUnitName)).Distinct())
            source.Append("import \"").Append(unit).AppendLine("\";");
        source.AppendLine("struct SceneTransparencyProjection {");
        for (int index = 0; index < materials.Length; index++)
            source.Append(FindTransparencyType(materials[index].Shader)).Append(".Projection Material").Append(index).AppendLine(";");
        source.AppendLine("};");
        source.AppendLine("struct SceneTransparencyMaterials : ITransparencyMaterials {");
        source.AppendLine("typealias Projection = SceneTransparencyProjection;");
        for (int index = 0; index < materials.Length; index++)
        {
            source.Append(FindTransparencyType(materials[index].Shader)).Append(" Material").Append(index);
            if (resourceCapacities[index] > 1) source.Append('[').Append(resourceCapacities[index]).Append(']');
            source.AppendLine(";");
        }
        source.AppendLine("Projection beginProjection(uint bin, uint source, uint view, float4 screenBounds) { Projection value = {};");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(index).Append("u) value.Material").Append(index).Append(" = ").Append(Resource(index))
                .Append(".beginProjection(source, view, ").Append(Tessellated(index)).AppendLine(", screenBounds);");
        source.AppendLine("return value; }");
        source.AppendLine("bool nextProjection(uint bin, uint source, uint view, inout Projection value, out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(index).Append("u) return ").Append(Resource(index))
                .Append(".nextProjection(source, view, ").Append(Tessellated(index)).Append(", value.Material").Append(index)
                .AppendLine(", primitive, domain, first, second, third);");
        source.AppendLine("primitive=0u; domain={}; first=second=third=0.0.xxxx; return false; }");
        AppendMethod("bool cover(uint bin, uint source, float3x2 domain, uint3 pixel, float depth, out float resultDepth)",
            "cover(source, domain, TESS, pixel, depth, resultDepth)", "return false;");
        source.AppendLine("float4 shade(uint bin, uint source, uint primitive, uint3 pixel, float depth) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(index).Append("u) return ").Append(Resource(index))
                .Append(".shade<").Append((uint)materials[index].VisibilityDepth).AppendLine("u>(source, primitive, pixel, depth);");
        source.AppendLine("return 0.0.xxxx; }");
        source.AppendLine("void receiver(uint bin, uint source, uint primitive, uint3 pixel, float depth, out float3 position, out float3 normal, out uint flags) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(index).Append("u) { ").Append(Resource(index))
                .Append(".receiver<").Append((uint)materials[index].VisibilityDepth).AppendLine("u>(source, primitive, pixel, depth, position, normal, flags); return; }");
        source.AppendLine("position=0.0.xxx; normal=0.0.xxx; flags=0u; }");
        source.AppendLine("};");
        string[] geometryEntries = ["ClearTransparentTiles", "CopyTransparentSources", "BuildTransparentGeometry", "ReserveTransparentCells", "ScatterTransparentCells"];
        string[] geometryCalls = ["ClearTransparency(thread)", "CopyTransparencySources(thread)", "BuildTransparency(materials, thread)", "ReserveTransparency(thread)", "ScatterTransparency(thread)"];
        for (int entry = 0; entry < geometryEntries.Length; entry++)
            source.Append("[shader(\"compute\")] [numthreads(64,1,1)] void ").Append(geometryEntries[entry])
                .Append(entry == 2 ? "(uniform SceneTransparencyMaterials materials, " : "(")
                .Append("uint3 thread : SV_DispatchThreadID) { ")
                .Append(geometryCalls[entry]).AppendLine("; }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void RequestTransparentShadows(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { RequestTransparencyShadows(materials, pixel); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveTransparency(materials, pixel); }");
        return source.ToString();

        void AppendMethod(string signature, string call, string fallback)
        {
            source.Append(signature).AppendLine(" {");
            for (int index = 0; index < materials.Length; index++)
                source.Append("if (bin == ").Append(index).Append("u) return ").Append(Resource(index)).Append('.').Append(call.Replace("TESS", Tessellated(index))).AppendLine(";");
            source.AppendLine(fallback).AppendLine("}");
        }

        string Resource(int index) => "Material" + index + (resourceCapacities[index] == 1 ? "" :
            "[NonUniformResourceIndex(TransparencyResourceItem(source))]");
        string Tessellated(int index) => materials[index].Shader.TryGetSlangEntry(0,
            ClusterMaterialEntries.TessellationSeed, SlangStage.Compute, out _) ? "true" : "false";
    }

    private static string FindTransparencyType(Shader shader)
    {
        string? name = null;
        for (int unit = 0; unit < shader.SlangSourceUnitCount; unit++) Visit(shader.GetSlangSourceUnit(unit).GetModuleReflection());
        return name ?? throw new InvalidDataException($"Material shader '{shader.Name}' requires a TransparencyMaterial evaluator.");
        void Visit(DeclReflection declaration)
        {
            if (declaration == DeclReflection.Null) return;
            if (declaration.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = declaration.Type;
                for (uint attribute = 0; attribute < type.AttributeCount; attribute++)
                    if (type.GetAttribute(attribute).Name == "TransparencyMaterial") name = type.Name;
            }
            for (uint child = 0; child < declaration.ChildrenCount; child++) Visit(declaration.GetChild(child));
        }
    }

    private static List<Material> GetTransparencyEntryMaterials(TransparencyData data, VariableLayoutReflection entry)
    {
        var result = new List<Material>();
        for (uint field = 0; field < entry.TypeLayout.FieldCount; field++)
        {
            VariableLayoutReflection parameter = entry.TypeLayout.GetFieldByIndex(field);
            if (parameter.Name != "materials") continue;
            for (uint evaluator = 0; evaluator < parameter.TypeLayout.FieldCount; evaluator++)
            {
                ClusterShadeInvocation[] materials = data.ResourceMaterials[evaluator];
                var resources = ClusterLinkedStageLayout.Create(parameter.TypeLayout.GetFieldByIndex(evaluator));
                foreach (var group in resources.Resources.ToArray().GroupBy(resource => (resource.Name, resource.Type)))
                    for (uint item = 0; item < data.Shaders.ResourceCapacities[evaluator]; item++)
                        foreach (var resource in group)
                            result.Add(materials[Math.Min((int)item, materials.Length - 1)].Material);
            }
        }
        return result;
    }

}

using SomeEngine.Render.Assets;
using System.Text;
using System.Numerics;
using System.Globalization;
using SlangShaderSharp;
using SomeEngine.Render.Tiles;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct TransparencyMember(Shader Shader, ClusterVisibilityDepth VisibilityDepth);

    private sealed class TransparencyShaders(TransparencyMember[] members, uint[] resourceCapacities,
        TileShaders pipelines, bool tileColor, bool splitSorting, string[] colorEntries)
    {
        internal readonly TransparencyMember[] Members = members;
        internal readonly uint[] ResourceCapacities = resourceCapacities;
        internal readonly TileShaders Pipelines = pipelines;
        internal readonly bool TileColor = tileColor;
        internal readonly bool SplitSorting = splitSorting;
        internal string[] ColorEntries = colorEntries;
        internal bool UsesUnorderedColor => Pipelines.ContainsKey("ResolveUnorderedTransparentColor");
        internal readonly uint TriangleStride = GetStride(pipelines["BuildTransparentGeometry"], "TransparencyTriangle");
        internal readonly uint SourceBoundsStride = GetStride(pipelines["BuildTransparentGeometry"], "TransparencySourceBounds");
        private static uint GetStride(TileComputeShader compiled, string name)
        {
            ShaderReflection reflection = compiled.Pipeline.Reflection;
            TypeReflection type = reflection.FindTypeByName(name)!.Value;
            return checked((uint)reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer)!.Value.GetSize(SlangParameterCategory.Uniform));
        }
    }

    private readonly List<TransparencyShaders> _transparencyShaders = [];

    private TransparencyShaders GetTransparencyShaders(ClusterShadeInvocation[] materials, bool tileColor, out ClusterShadeInvocation[][] resources)
    {
        TransparencyMember[] members = materials.Select(stage =>
            new TransparencyMember(stage.Material.Shader!, stage.VisibilityDepth)).Distinct()
            .OrderBy(member => member.Shader.GetSlangSourceUnitName(member.Shader.SlangSourceUnitCount - 1), StringComparer.Ordinal)
            .ThenBy(member => member.VisibilityDepth).ToArray();
        foreach (TransparencyShaders existing in _transparencyShaders)
        {
            if (existing.TileColor != tileColor || existing.Members.Length != members.Length || !existing.Members.All(members.Contains)) continue;
            resources = GroupResources(existing.Members);
            if (resources.Select((group, index) => (uint)group.Length <= existing.ResourceCapacities[index]).All(value => value))
                return existing;
        }

        resources = GroupResources(members);
        uint[] capacities = resources.Select(group => BitOperations.RoundUpToPowerOf2((uint)group.Length)).ToArray();
        Shader source = members[0].Shader;
        string[] arguments = [((uint)members[0].VisibilityDepth).ToString(CultureInfo.InvariantCulture),
            source.TryGetSlangEntry(0, ClusterMaterialEntries.TessellationSeed, SlangStage.Compute, out _) ? "true" : "false"];
        bool pixelLists = UseTransparencyReference;
        bool splitSorting = !pixelLists && tileColor && members.All(member =>
            member.Shader.TryGetSlangEntry(0,"PrepareTransparentCells",SlangStage.Compute,out _));
        Shader? reference = null;
#if SOMEENGINE_TRANSPARENCY_RESEARCH
        if (pixelLists)
        {
            using Stream shader = typeof(ClusterPipelineSystem).Assembly.GetManifestResourceStream("ClusterTransparencyPixelListReference")!;
            using var reader = new StreamReader(shader);
            reference = source.Compose("PPLL reference", "cluster_pixel_list_reference.slang",
                "import \"cluster_transparency\";\n" + reader.ReadToEnd(),[],_device.Capabilities.CompileTarget);
        }
#endif
        bool unordered = !pixelLists && !tileColor && members.Length == 1 && capacities[0] == 1 &&
            source.TryGetSpecialization("ResolveUnorderedTransparentColor", arguments, out _) &&
            source.TryGetSpecialization("ResolveProjectedUnorderedTransparentColor", arguments, out _);
        string[] colorEntries = pixelLists ? ["ResolveTransparentPixelLists"] : tileColor
            ? ["ResolveTileTransparentColor", "ResolveLocalTileTransparentColor", "ResolveDepthTileTransparentColor", "ResolveProjectedTransparentColor"]
            : unordered ? ["ResolveUnorderedTransparentColor", "ResolveProjectedUnorderedTransparentColor"]
            : ["ResolveTransparentColor", "ResolveProjectedTransparentColor"];
        bool saved = !pixelLists && _options.DebugMode == 0 && members.Length == 1 && capacities[0] == 1 &&
            colorEntries.Concat(["BuildTransparentGeometry"])
                .All(entry => source.TryGetSpecialization(entry, arguments, out _));
        if (saved && colorEntries.Any(entry => HasVirtualShadowInputs(source.RequireSpecialization(entry, arguments), entry)))
            saved = source.TryGetSpecialization("RequestTransparentPages", arguments, out _);
        Shader? composition = saved ? null : ComposeTransparencyShaders(members,capacities,_device.Capabilities.CompileTarget,_options.DebugMode,reference,tileColor,splitSorting);
        Shader hardware = reference ?? (_options.ForceHardwareRaster ? ComposeTransparencyHardware(source) : source);
        var pipelines = new TileShaders(_backend, _device, hardware);
        foreach (string entry in new[] { "ClearTransparentTiles", "CopyTransparentSources", "BuildTransparentGeometry" })
            AddPipeline(entry);
        if (!pixelLists)
            foreach (string entry in new[] { "CountTransparentCells", "ReserveTransparentCells", "ScatterTransparentCells",
                         "ScatterTransparentHardwareCells", "CountTransparentHardwareCells",
                         "ClassifyTransparentTiles", "FinalizeTransparentTiles", "ClearTransparentHardwareMasks" }) AddPipeline(entry);
        if (!pixelLists)
            foreach (string entry in splitSorting ? new[] { "PrepareTransparentCells", "FinalizeTransparentSort", "SortTransparentRanges" }
                         : new[] { "SortTransparentCells" }) AddPipeline(entry);
#if SOMEENGINE_TRANSPARENCY_RESEARCH
        if (pixelLists)
            foreach (string entry in new[] { "PrepareTransparentPixelLists", "BuildTransparentPixelLists" }) AddPipeline(entry);
#endif
        foreach (string entry in colorEntries) AddPipeline(entry);
        if (colorEntries.Any(UsesVirtualShadows))
        {
            AddPipeline("RequestTransparentPages");
        }
        var result = new TransparencyShaders(members, capacities, pipelines, tileColor, splitSorting, colorEntries);
        _transparencyShaders.Add(result);
        return result;

        void AddPipeline(string entry)
        {
            Shader shader = entry is "PrepareTransparentPixelLists" or "BuildTransparentPixelLists" ? reference! : composition ?? (entry is "BuildTransparentGeometry" or "RequestTransparentPages" or "ResolveTransparentColor" or
                "ResolveProjectedTransparentColor" or "ResolveTransparentPixelLists" or "ResolveTileTransparentColor" or "ResolveLocalTileTransparentColor" or "ResolveDepthTileTransparentColor" or "ResolveUnorderedTransparentColor" or "ResolveProjectedUnorderedTransparentColor"
                ? source.RequireSpecialization(entry, arguments) : source);
            pipelines.Add(entry, shader);
        }

        bool UsesVirtualShadows(string entry) => HasVirtualShadowInputs(pipelines[entry].Shader, entry);

        bool HasVirtualShadowInputs(Shader shader, string entry)
        {
            ShaderParameterLayout layout = shader.GetSlangResourceLayout(_device.Capabilities.CompileTarget, entry);
            return layout.Contains("VirtualShadowSampling") || layout.Contains("VirtualShadowPageTable");
        }

        ClusterShadeInvocation[][] GroupResources(TransparencyMember[] types) => types.Select(member => materials
            .Where(stage => ReferenceEquals(stage.Material.Shader, member.Shader) && stage.VisibilityDepth == member.VisibilityDepth)
            .DistinctBy(stage => stage.CommandBindings).ToArray()).ToArray();
    }

    private Shader ComposeTransparencyHardware(Shader source)
    {
        using Stream shader = typeof(ClusterPipelineSystem).Assembly.GetManifestResourceStream("ClusterTransparencyHardware")!;
        using var reader = new StreamReader(shader);
        return source.Compose("Tile hardware coverage", "tile_transparency_hardware.slang",
            "#define CLUSTER_TRANSPARENCY_HARDWARE_ENTRIES\n" + reader.ReadToEnd(), [], _device.Capabilities.CompileTarget);
    }

    private static string[] GetTransparencyArguments(TransparencyMember member) =>
        [((uint)member.VisibilityDepth).ToString(CultureInfo.InvariantCulture),
            member.Shader.TryGetSlangEntry(0, ClusterMaterialEntries.TessellationSeed, SlangStage.Compute, out _) ? "true" : "false"];

    private void PrepareOrderedTransparencyShaders(TransparencyShaders shaders)
    {
        if (!shaders.UsesUnorderedColor || shaders.Pipelines.ContainsKey("ResolveOrderedTransparentColor")) return;
        Shader material = shaders.Members[0].Shader;
        string[] arguments = GetTransparencyArguments(shaders.Members[0]);
        string[] entries = ["ResolveOrderedTransparentColor", "ResolveProjectedOrderedTransparentColor"];
        Shader? composition = null;
        if (!entries.All(entry => material.TryGetSpecialization(entry, arguments, out _)))
        {
            composition = material.Compose("Cluster ordered transparent material", "scene_ordered_transparency.slang",
                CreateTransparencySource(shaders.Members, shaders.ResourceCapacities, _options.DebugMode, false, shaders.TileColor, shaders.SplitSorting, orderedColorOnly: true),
                [], _device.Capabilities.CompileTarget);
        }
        foreach (string entry in entries)
        {
            Shader shader = composition ?? material.RequireSpecialization(entry, arguments);
            shaders.Pipelines.Add(entry, shader);
        }
        shaders.ColorEntries = [.. shaders.ColorEntries, .. entries];
    }

    private static Shader ComposeTransparencyShaders(TransparencyMember[] materials, uint[] resourceCapacities, SlangCompileTarget target, uint debugMode, Shader? reference, bool tileColor, bool splitSorting)
        => materials[0].Shader.Compose("Cluster transparent materials", "scene_transparency.slang",
            CreateTransparencySource(materials,resourceCapacities,debugMode,reference is not null,tileColor,splitSorting),
            materials.Skip(1).Select(stage => stage.Shader).Concat(reference is null ? [] : new[] { reference }).ToArray(), target);

    private static string CreateTransparencySource(TransparencyMember[] materials, uint[] resourceCapacities, uint debugMode, bool pixelLists, bool tileColor, bool splitSorting, bool orderedColorOnly = false)
    {
        var source = new StringBuilder();
        foreach (string unit in materials.SelectMany(stage => Enumerable.Range(0, stage.Shader.SlangSourceUnitCount)
                     .Select(stage.Shader.GetSlangSourceUnitName)).Distinct())
            source.Append("import \"").Append(unit).AppendLine("\";");
#if SOMEENGINE_TRANSPARENCY_RESEARCH
        if (pixelLists) source.AppendLine("import \"cluster_pixel_list_reference\";");
#endif
        source.AppendLine("struct SceneTransparencyProjection {");
        for (int index = 0; index < materials.Length; index++)
            source.Append(FindTransparencyType(materials[index].Shader)).Append(".Projection Material").Append(index).AppendLine(";");
        source.AppendLine("};");
        source.AppendLine("struct SceneTransparencyShadingData {");
        for (int index = 0; index < materials.Length; index++)
            source.Append(FindTransparencyType(materials[index].Shader)).Append(".SourceData Material").Append(index).AppendLine(";");
        source.AppendLine("};");
        source.AppendLine("groupshared SceneTransparencyShadingData SceneTransparencyTileShading[16];");
        source.AppendLine("struct SceneTransparencyMaterials : ITransparencyMaterials {");
        source.AppendLine("typealias Projection = SceneTransparencyProjection;");
        source.AppendLine("typealias Shading = SceneTransparencyShadingData;");
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
        source.AppendLine("Shading readProjectionShading(uint bin, uint source, Projection value) { Shading result = {};");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(index).Append("u) result.Material").Append(index)
                .Append(" = ").Append(Resource(index)).Append(".sourceData(value.Material").Append(index).AppendLine(");");
        source.AppendLine("return result; }");
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
        source.AppendLine("Shading evaluateShading(uint index) { TransparencyTriangle triangle = TransparencyRasterTriangles[index]; uint source = triangle.Source;");
        source.AppendLine("Projection value = beginProjection(triangle.Bin,source,triangle.View,float4(.5,.5,float(TransparencyUniforms.Width)-.5,float(TransparencyUniforms.Height)-.5));");
        source.AppendLine("return readProjectionShading(triangle.Bin,source,value); }");
        source.AppendLine("void cacheTileShading(uint packet, Shading value) { SceneTransparencyTileShading[packet] = value; }");
        source.AppendLine("Shading loadTileShading(uint packet) { return SceneTransparencyTileShading[packet]; }");
        source.AppendLine("Shading readShadingLane(Shading value, uint lane) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("value.Material").Append(index).Append(" = Material").Append(index)
                .Append(resourceCapacities[index] > 1 ? "[0]" : "").Append(".readSourceData(value.Material").Append(index).AppendLine(",lane);");
        source.AppendLine("return value; }");
        foreach (bool coverage in new[] { true, false })
        {
            source.AppendLine(coverage
                ? "bool coverShading(uint bin, uint source, Shading value, float3x2 domain, uint3 pixel, float depth, out float resultDepth) {"
                : "float4 shadeShading(uint bin, uint source, Shading value, uint3 pixel, float depth) {");
            for (int index = 0; index < materials.Length; index++)
            {
                source.Append("if (bin == ").Append(index).Append("u) {");
                if (coverage && materials[index].VisibilityDepth == ClusterVisibilityDepth.Opaque)
                    source.AppendLine("resultDepth=depth; return true; }");
                else
                {
                    source.Append("return ").Append(Resource(index)).Append(coverage ? ".coverData<" : ".shadeData<")
                        .Append((uint)materials[index].VisibilityDepth).Append('u');
                    if (!coverage) source.Append(',').Append(debugMode).Append('u');
                    source.Append(">(value.Material").Append(index).Append(',');
                    if (coverage) source.Append("domain,").Append(Tessellated(index)).Append(',');
                    source.Append("pixel,depth").Append(coverage ? ",resultDepth" : "").AppendLine("); }");
                }
            }
            source.AppendLine(coverage ? "resultDepth=depth; return false; }" : "return 0.0.xxxx; }");
        }
        foreach (bool coverage in new[] { true, false })
        {
            source.AppendLine(coverage
                ? "bool coverTriangle(uint bin, uint index, float3x2 domain, uint3 pixel, float depth, out float resultDepth) {"
                : "float4 shadeTriangle(uint bin, uint index, uint3 pixel, float depth) {");
            source.AppendLine("uint source = TransparencyRasterTriangles[index].Source;");
            for (int index = 0; index < materials.Length; index++)
            {
                source.Append("if (bin == ").Append(index).AppendLine("u) {");
                if (coverage && materials[index].VisibilityDepth == ClusterVisibilityDepth.Opaque)
                {
                    source.AppendLine("resultDepth = depth; return true; }");
                    continue;
                }
                source.Append(FindTransparencyType(materials[index].Shader)).Append(".SourceData value = evaluateShading(index).Material").Append(index).AppendLine(";");
                source.Append("return ").Append(Resource(index)).Append(coverage ? ".coverData<" : ".shadeData<")
                    .Append((uint)materials[index].VisibilityDepth).Append(coverage ? "u>(value,domain," : $"u,{debugMode}u>(value,");
                if (coverage) source.Append(Tessellated(index)).Append(',');
                source.Append("pixel,depth").Append(coverage ? ",resultDepth" : "").AppendLine("); }");
            }
            source.AppendLine(coverage ? "resultDepth = depth; return false; }" : "return 0.0.xxxx; }");
        }
        source.AppendLine("};");
        if (orderedColorOnly)
        {
            source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveOrderedTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveOrderedTransparency<SceneTransparencyMaterials,false>(materials, pixel); }");
            source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveProjectedOrderedTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveOrderedTransparency<SceneTransparencyMaterials,true>(materials, pixel); }");
            return source.ToString();
        }
        string[] geometryEntries = ["ClearTransparentTiles", "CopyTransparentSources", "BuildTransparentGeometry", "CountTransparentCells", "ReserveTransparentCells", "ScatterTransparentCells"];
        string[] geometryCalls = ["ClearTransparency(thread)", "CopyTransparencySources(thread)", "BuildTransparency(materials, thread)", "CountTransparencyCells(group, thread)", "ReserveTransparency(thread)", "ScatterTransparency(group, thread)"];
        for (int entry = 0; entry < (pixelLists ? 3 : geometryEntries.Length); entry++)
            source.Append("[shader(\"compute\")] [numthreads(64,1,1)] void ").Append(geometryEntries[entry])
                .Append(entry == 2 ? "(uniform SceneTransparencyMaterials materials, " : "(")
                .Append(entry is 3 or 5 ? "uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { " : "uint3 thread : SV_DispatchThreadID) { ")
                .Append(geometryCalls[entry]).AppendLine("; }");
        source.AppendLine("[shader(\"compute\")] [numthreads(256,1,1)] void RequestTransparentPages(uniform SceneTransparencyMaterials materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { RequestTransparencyPages(materials,group,thread); }");
#if SOMEENGINE_TRANSPARENCY_RESEARCH
        if (pixelLists)
        {
            source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveTransparentPixelLists(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveTransparencyPixelLists(materials,pixel); }");
            return source.ToString();
        }
#endif
        source.AppendLine("[shader(\"compute\")] [numthreads(128,1,1)] [WaveSize(32)] void SortTransparentCells(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { SortTransparencyCells(group, thread); }");
        if (splitSorting)
        {
            source.AppendLine("[shader(\"compute\")] [numthreads(128,1,1)] [WaveSize(32)] void PrepareTransparentCells(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { PrepareTransparencyCells(group,thread); }");
            source.AppendLine("[shader(\"compute\")] [numthreads(1,1,1)] void FinalizeTransparentSort() { FinalizeTransparencySort(); }");
            source.AppendLine("[shader(\"compute\")] [numthreads(128,1,1)] [WaveSize(32)] void SortTransparentRanges(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { SortTransparencyRanges(group,thread); }");
        }
        source.Append("[shader(\"compute\")] [numthreads(64,1,1)] void ClassifyTransparentTiles(uint3 thread : SV_DispatchThreadID) { ")
            .Append(splitSorting ? "ClassifyTransparencySortedTiles" : "ClassifyTransparencyTiles").AppendLine("(thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] void ScatterTransparentHardwareCells(uint3 thread : SV_DispatchThreadID) { ScatterTransparencyHardwareCells(thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] void CountTransparentHardwareCells(uint3 thread : SV_DispatchThreadID) { CountTransparencyHardwareCells(thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(1,1,1)] void FinalizeTransparentTiles() { FinalizeTransparencyTiles(); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] void ClearTransparentHardwareMasks(uint3 thread : SV_DispatchThreadID) { ClearTransparencyHardwareMasks(thread); }");
        if (!tileColor) source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveTransparency<SceneTransparencyMaterials,false>(materials, pixel); }");
        if (tileColor) source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] [WaveSize(32)] void ResolveTileTransparentColor(uniform SceneTransparencyMaterials materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { ResolveTransparencyTiles(materials, group, thread); }");
        if (tileColor) source.AppendLine("[shader(\"compute\")] [numthreads(128,1,1)] [WaveSize(32)] void ResolveLocalTileTransparentColor(uniform SceneTransparencyMaterials materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { ResolveTransparencyLocalTileJobs(materials, group, thread); }");
        if (tileColor) source.AppendLine("[shader(\"compute\")] [numthreads(256,1,1)] [WaveSize(32)] void ResolveDepthTileTransparentColor(uniform SceneTransparencyMaterials materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { ResolveTransparencyDepthTileJobs(materials, group, thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveProjectedTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveTransparency<SceneTransparencyMaterials,true>(materials, pixel); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveUnorderedTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveUnorderedTransparency<SceneTransparencyMaterials,false>(materials, pixel); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveProjectedUnorderedTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveUnorderedTransparency<SceneTransparencyMaterials,true>(materials, pixel); }");
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
                var resources = ShaderParameterLayout.FromReflection(parameter.TypeLayout.GetFieldByIndex(evaluator));
                foreach (var group in resources.Resources.ToArray().GroupBy(resource => (resource.Name, resource.Type)))
                    for (uint item = 0; item < data.Shaders.ResourceCapacities[evaluator]; item++)
                        foreach (var resource in group)
                            result.Add(materials[Math.Min((int)item, materials.Length - 1)].Material);
            }
        }
        return result;
    }

}

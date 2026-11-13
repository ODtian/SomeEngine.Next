using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ArchitectureRules;

public readonly record struct ArchitectureViolation(string Rule, string Path, int Line, int Column, string Description);

public static class VsmTileRules
{
    private static readonly string[] SurfaceProtocols =
        ["ITransparentSurfaceEvaluate", "markShadowPages", "evaluateTransparency"];
    private static readonly string[] CandidateLayerData =
    [
        "LayerSortTileLists", "LayerRetireClusters", "CandidatesPerRound", "MaximumRounds", "CandidateBudget",
        "IncompletePixels", "LayerDeferredPageRequests", "LayerCandidateOutput", "LayerPrepareRound",
    ];
    private static readonly string[] LightGridTypes = ["ClusteredLightGridParameters", "LightingFeature", "LightingViews"];
    private static readonly string[] ViewResetNames = ["viewLayoutChanged", "_forceStateReset", "_previousForceStateReset"];
    private static readonly string[] Collections =
    [
        "List", "Dictionary", "HashSet", "Queue", "Stack", "LinkedList", "SortedList", "SortedSet", "SortedDictionary",
        "ConcurrentDictionary", "ConcurrentQueue", "ConcurrentStack", "ConcurrentBag", "BlockingCollection", "ArrayList", "Hashtable",
    ];
    private static readonly string[] MaterialProperties = ["BaseColor", "Roughness", "Metallic", "Occlusion"];
    private static readonly string[] MaterialEngineResources =
    [
        "MaterialViews", "MaterialDepth", "MaterialInstanceData", "MaterialInstanceProperties",
        "LightingViews", "LightBuffer", "ClusteredLightGrid", "LightIndexList", "LightingCookieAtlas",
        "MaterialDecalUniforms", "MaterialDecalVolumes", "MaterialDecalProperties",
    ];
    private static readonly string[] MaterialLibraryFiles =
    [
        "assets/Shaders/standard_material.slang",
        "assets/Shaders/standard_material_debug.slang",
        "assets/Shaders/unlit_material.slang",
        "assets/Shaders/coating_material.slang",
        "assets/Shaders/brdf.slang",
    ];

    public static IReadOnlyList<ArchitectureViolation> Check(string repository, IEnumerable<string>? rules = null, IEnumerable<string>? paths = null)
    {
        var selectedRules = rules?.ToHashSet(StringComparer.Ordinal);
        string[]? selectedPaths = paths?.Select(path => path.Replace('\\', '/').TrimEnd('/')).ToArray();
        var result = new List<ArchitectureViolation>();
        string[] files = SourceFiles(repository).ToArray();
        var shaderSources = new Dictionary<string, ShaderSyntax>(StringComparer.Ordinal);
        ShaderSyntax ReadShaderSource(string path)
        {
            if (!shaderSources.TryGetValue(path, out ShaderSyntax? syntax))
                shaderSources.Add(path, syntax = new(File.ReadAllText(path)));
            return syntax;
        }
        bool rasterSpillStorage = new[] { "cluster_bin_reserve.slang", "cluster_bin_scatter.slang" }
            .Select(name => Path.Combine(repository, "assets", "Shaders", name))
            .Any(path => File.Exists(path) && ReadShaderSource(path).HasResource("RasterSpillClusters"));
        var engineResources = new HashSet<string>(MaterialEngineResources, StringComparer.Ordinal);
        if (selectedRules is null || selectedRules.Contains("ARCH-24"))
            foreach (string file in files.Where(file => Path.GetExtension(file) == ".slang"))
            {
                string relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
                if (!relative.StartsWith("assets/Shaders/", StringComparison.Ordinal)) continue;
                ShaderSyntax syntax = ReadShaderSource(file);
                if (!IsMaterialLibrary(relative, syntax))
                    engineResources.UnionWith(syntax.Resources.Select(resource => resource.Name));
            }
        bool MaterialImport(string shaderPath, string name)
        {
            string sourceName = (name.EndsWith(".slang", StringComparison.Ordinal) ? name[..^6] : name)
                .Replace('.', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar) + ".slang";
            string[] candidates = [Path.Combine(repository, Path.GetDirectoryName(shaderPath)!, sourceName),
                Path.Combine(repository, "assets", "Shaders", sourceName)];
            foreach (string candidate in candidates.Distinct())
                if (File.Exists(candidate))
                    return IsMaterialLibrary(Path.GetRelativePath(repository, candidate).Replace('\\', '/'), ReadShaderSource(candidate));
            string leaf = ImportLeaf(name);
            foreach (string candidate in files.Where(file => Path.GetExtension(file) == ".slang"
                && Path.GetFileNameWithoutExtension(file) == leaf))
                if (IsMaterialLibrary(Path.GetRelativePath(repository, candidate).Replace('\\', '/'), ReadShaderSource(candidate))) return true;
            return MaterialLibraryFiles.Any(file => Path.GetFileNameWithoutExtension(file) == leaf);
        }
        foreach (string path in files)
        {
            string relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
            if (selectedPaths is not null && !selectedPaths.Any(prefix => relative == prefix || relative.StartsWith(prefix + '/', StringComparison.Ordinal)))
                continue;
            string source = File.ReadAllText(path);
            void Report(string rule, int offset, string description)
            {
                if (selectedRules is not null && !selectedRules.Contains(rule)) return;
                var position = Microsoft.CodeAnalysis.Text.SourceText.From(source).Lines.GetLinePosition(offset);
                result.Add(new(rule, relative, position.Line + 1, position.Character + 1, description));
            }

            if (Path.GetExtension(path) == ".slang")
                ReadShader(relative, ReadShaderSource(path), name => MaterialImport(relative, name), engineResources,
                    rasterSpillStorage, Report);
            else
                ReadCSharp(relative, ReadCSharpTree(source), Report);
            if (IsRetiredCompositionFile(relative))
                Report("ARCH-27", 0, "Layer composition and independent decal binning replace this file.");
        }
        return result;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
            if (Path.GetExtension(path) is ".cs" or ".slang" && !path.EndsWith(".ir.slang", StringComparison.Ordinal))
                yield return path;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "external" or "artifacts" || name.StartsWith('.')) continue;
            foreach (string path in SourceFiles(child)) yield return path;
        }
    }

    private static SyntaxNode ReadCSharpTree(string source)
    {
        // Include all conditional branches while preserving file positions.
        var characters = source.ToCharArray();
        foreach (SyntaxTrivia trivia in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTrivia(descendIntoTrivia: true))
            if (trivia.GetStructure() is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax or EndIfDirectiveTriviaSyntax)
                for (int index = trivia.Span.Start; index < trivia.Span.End; index++)
                    if (characters[index] is not ('\r' or '\n')) characters[index] = ' ';
        return CSharpSyntaxTree.ParseText(new string(characters)).GetRoot();
    }

    private static void ReadCSharp(string path, SyntaxNode root, Action<string, int, string> report)
    {
        bool lighting = path.StartsWith("src/SomeEngine.Render.Features/Lighting/", StringComparison.Ordinal);
        bool shadows = IsShadowCode(path);
        bool layersOrDecals = IsLayerOrDecalCode(path);
        foreach (SyntaxToken token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                if (SurfaceProtocols.Contains(token.ValueText) || token.ValueText == "MarkVirtualShadowPages")
                    report("ARCH-25", token.SpanStart, $"Material protocol member {token.ValueText} belongs to engine surface processing.");
                if (path.StartsWith("src/", StringComparison.Ordinal) && CandidateLayerData.Contains(token.ValueText))
                    report("ARCH-38", token.SpanStart, $"Layer composition stores all fragments in the tile block pool: {token.ValueText} belongs to the replaced candidate path.");
                if (lighting && (token.ValueText.Contains("SinglePoint", StringComparison.Ordinal) || token.ValueText.Contains("GroupsPerLight", StringComparison.Ordinal)))
                    report("ARCH-30", token.SpanStart, $"Lighting uses the shared grid path: {token.ValueText}.");
                if (shadows && ViewResetNames.Contains(token.ValueText))
                    report("ARCH-31", token.SpanStart, $"View history belongs to each light key: {token.ValueText}.");
                if (layersOrDecals && LightGridTypes.Contains(token.ValueText))
                    report("ARCH-35", token.SpanStart, $"Layers and decals provide their own grid: {token.ValueText}.");
            }
            else if (lighting && token.IsKind(SyntaxKind.StringLiteralToken) && token.ValueText.Contains("-v2", StringComparison.Ordinal))
                report("ARCH-30", token.SpanStart, "Lighting entry names describe their behavior.");
        }

        if (!shadows) return;
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            if (CallName(call) == "ReferenceEquals" && call.ArgumentList.Arguments.Any(argument =>
                argument.Expression.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "_frameInstances")))
                report("ARCH-31", call.SpanStart, "Instance changes use ContentRevision.");

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText == "PrepareViews")
                foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
                    if (parameter.Identifier.ValueText is "geometryRevision" or "rasterRevision" or "instanceCount")
                        report("ARCH-31", parameter.SpanStart, "PrepareViews receives view data; caster bounds drive content invalidation.");
            if (method.Identifier.ValueText is not ("StageFrame" or "PrepareViews" or "SelectDistantUpdates" or "OnFrameSubmitted")) continue;
            SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody;
            if (body is null) continue;
            foreach (SyntaxNode allocation in body.DescendantNodesAndSelf().Where(node => node is ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax
                || node is ObjectCreationExpressionSyntax creation && HasCollectionType(creation.Type)
                || node is ImplicitObjectCreationExpressionSyntax implicitCreation && AllocationType(implicitCreation, root) is { } type && HasCollectionType(type)))
                report("ARCH-33", allocation.SpanStart, $"{method.Identifier.ValueText} reuses member storage.");
            foreach (InvocationExpressionSyntax call in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                string? name = CallName(call);
                if (name is "Select" or "Where" or "ToArray" or "ToList")
                    report("ARCH-33", call.SpanStart, $"{method.Identifier.ValueText} traverses reusable storage directly: {name}.");
                if (name is "AddCopyPass" or "CreateBuffer")
                    report("ARCH-33", call.SpanStart, $"{method.Identifier.ValueText} updates persistent graph buffers: {name}.");
            }
        }
    }

    private static bool HasCollectionType(TypeSyntax type) => type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(name => Collections.Contains(name.Identifier.ValueText));

    private static TypeSyntax? AllocationType(ImplicitObjectCreationExpressionSyntax creation, SyntaxNode root)
    {
        if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable })
            return (variable.Parent as VariableDeclarationSyntax)?.Type;
        if (creation.Parent is AssignmentExpressionSyntax assignment)
        {
            string? name = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => null,
            };
            TypeSyntax? field = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(variable => variable.Identifier.ValueText == name)?.Parent is VariableDeclarationSyntax declaration
                ? declaration.Type : null;
            return field ?? root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(property => property.Identifier.ValueText == name)?.Type;
        }
        return creation.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax
            ? creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ReturnType : null;
    }
    private static string? CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static void ReadShader(string path, ShaderSyntax syntax, Func<string, bool> materialImport, HashSet<string> engineResources,
        bool rasterSpillStorage, Action<string, int, string> report)
    {
        string file = Path.GetFileName(path);
        bool engine = path.StartsWith("assets/Shaders/", StringComparison.Ordinal);
        bool shadows = engine && file.StartsWith("vsm_", StringComparison.Ordinal);
        bool lighting = engine && file is "clustered_light_grid.slang" or "clustered_light_binning.slang";
        bool layerOrDecal = engine && (file.StartsWith("layer_", StringComparison.Ordinal) || file.StartsWith("material_decal", StringComparison.Ordinal));
        bool binning = engine && IsBinningShader(file);
        bool material = ImplementsMaterial(syntax) || path.StartsWith("samples/", StringComparison.Ordinal)
            && path.Contains("/assets/Shaders/", StringComparison.Ordinal) && IsMaterialShader(syntax);
        bool materialLibrary = IsMaterialLibrary(path, syntax);

        foreach (ShaderToken token in syntax.Tokens)
        {
            if (token.IsIdentifier)
            {
                if (engine && token.Text is "TargetPolicy" or "targetPolicy")
                    report("ARCH-23", token.Offset, "Raster output is selected by the output type argument.");
                if (material && (token.Text is "render_lighting" or "material_decals" or "TRANSPARENCY_MATERIAL_TYPE" or "outputColor"
                    || token.Text.StartsWith("vsm_", StringComparison.Ordinal)))
                    report("ARCH-24", token.Offset, $"Materials evaluate and shade their surfaces through public shader libraries; {token.Text} uses an engine resource detail.");
                if (SurfaceProtocols.Contains(token.Text))
                    report("ARCH-25", token.Offset, $"{token.Text} is replaced by shared material evaluation and visibility requests.");
                if (lighting && (token.Text.Contains("SinglePoint", StringComparison.Ordinal) || token.Text.Contains("GroupsPerLight", StringComparison.Ordinal)))
                    report("ARCH-30", token.Offset, $"Lighting uses the shared grid path: {token.Text}.");
                if (shadows && token.Text is "VSM_PAGE_LOCKED" or "VSM_PAGE_RECEIVER_DEPTH")
                    report("ARCH-32", token.Offset, $"Page state uses the retained flags: {token.Text} is obsolete.");
                if (layerOrDecal && LightGridTypes.Contains(token.Text))
                    report("ARCH-35", token.Offset, $"Layers and decals provide their own grid: {token.Text}.");
                if (engine && !materialLibrary && MaterialProperties.Contains(token.Text))
                    report("ARCH-36", token.Offset, $"{token.Text} is interpreted by material code; engine passes define their consumed output data.");
                if ((engine || path.StartsWith("src/", StringComparison.Ordinal)) && CandidateLayerData.Contains(token.Text))
                    report("ARCH-38", token.Offset, $"Layer composition stores all fragments in the tile block pool: {token.Text} belongs to the replaced candidate path.");
            }
            else if (lighting && token.IsString && token.Text.Contains("-v2", StringComparison.Ordinal))
                report("ARCH-30", token.Offset, "Lighting entry names describe their behavior.");
        }

        for (int index = 0; index < syntax.Tokens.Length; index++)
        {
            ShaderToken token = syntax.Tokens[index];
            if (!token.IsIdentifier || token.Text != "MarkVirtualShadowPages") continue;
            bool declaration = syntax.Functions.Any(function => function.NameIndex == index);
            bool library = path == "assets/Shaders/vsm_page_requests.slang";
            bool depthPass = path == "assets/Shaders/vsm_visibility_requests.slang";
            bool layerOutput = engine && syntax.ContainingType(index) == "LayerVisibilityOutput";
            if (!(declaration && library || syntax.IsCall(index) && (library || depthPass || layerOutput)))
                report("ARCH-25", token.Offset, "MarkVirtualShadowPages is defined by vsm_page_requests and called by the depth request pass or LayerVisibilityOutput.");
        }

        if (engine && file.StartsWith("layer_", StringComparison.Ordinal))
        {
            foreach (ShaderImport import in syntax.Imports.Where(import => materialImport(import.Name)))
                report("ARCH-39", syntax.Tokens[import.NameIndex].Offset, $"Layer sorting and composition consume shaded fragment payloads; {import.Name} belongs to material shading.");
            for (int index = 0; index < syntax.Tokens.Length; index++)
                if (syntax.Tokens[index].Text == "evaluateSurface" && syntax.IsCall(index))
                    report("ARCH-39", syntax.Tokens[index].Offset, "Layer fragment shading uses ShadeSurface with LayerFragmentInput and LayerPayloadOutput.");
        }

        if (material)
        {
            foreach (ShaderResource resource in syntax.Resources.Where(resource => engineResources.Contains(resource.Name)))
                report("ARCH-24", syntax.Tokens[resource.NameIndex].Offset, $"Engine resource {resource.Name} is declared by the imported engine shader library.");
            foreach (ShaderImport import in syntax.Imports.Where(import =>
                ImportLeaf(import.Name) is "render_lighting" or "material_decals"
                || ImportLeaf(import.Name).StartsWith("vsm_", StringComparison.Ordinal)))
                if (syntax.Tokens[import.NameIndex].IsString)
                    report("ARCH-24", syntax.Tokens[import.NameIndex].Offset, "Materials import public shading libraries that encapsulate engine resources and page requests.");
            for (int index = 0; index + 2 < syntax.Tokens.Length; index++)
            {
                ShaderToken[] tokens = syntax.Tokens;
                if (tokens[index].Text != "#") continue;
                if (tokens[index + 1].Text == "include" && tokens[index + 2].IsString)
                {
                    string included = Path.GetFileName(tokens[index + 2].Text.Trim('"'));
                    if (included.StartsWith("cluster_", StringComparison.Ordinal) || included.StartsWith("vsm_", StringComparison.Ordinal)
                        || included is "render_lighting.slang" or "material_decals.slang")
                        report("ARCH-24", tokens[index].Offset, "Material code uses public shader libraries; engine entry and resource declarations belong to engine inputs.");
                }
                else if (tokens[index + 1].Text == "define" && tokens[index + 2].Text.StartsWith("CLUSTER_", StringComparison.Ordinal))
                    report("ARCH-24", tokens[index].Offset, "Engine entries select raster and pass outputs; material code evaluates its surface.");
            }
        }

        if (binning)
        {
            if (!syntax.HasInclude("screen_binning.slang") && !syntax.HasImport("screen_binning"))
                report("ARCH-28", 0, "Screen binning consumers reference the shared screen_binning shader.");
            foreach (ShaderFunction function in syntax.Functions.Where(function => function.Name.StartsWith("Reserve", StringComparison.Ordinal)))
                report("ARCH-28", syntax.Tokens[function.NameIndex].Offset, $"{function.Name} allocates list offsets outside ScreenBinningScan.");
            for (int index = 0; index < syntax.Tokens.Length; index++)
                if (syntax.Tokens[index].Text.StartsWith("Reserve", StringComparison.Ordinal) && syntax.IsCall(index))
                    report("ARCH-28", syntax.Tokens[index].Offset, "List offsets are computed by ScreenBinningScan.");
            foreach (ShaderAssignment assignment in syntax.Assignments.Where(assignment =>
                assignment.Target == "ScreenBinningCells" && assignment.Component is "" or "x"
                || assignment.Target.Contains("CellOffset", StringComparison.Ordinal)
                || assignment.Target.Contains("ListOffset", StringComparison.Ordinal)
                || assignment.Target.Contains("TileOffset", StringComparison.Ordinal)))
                report("ARCH-28", syntax.Tokens[assignment.TargetIndex].Offset, $"{assignment.Target} offsets are written by ScreenBinningScanCells.");
            for (int index = 0; index < syntax.Tokens.Length; index++)
                if (syntax.Tokens[index].Text == "InterlockedAdd" && syntax.Parameters(index) is int arguments && arguments >= 0
                    && syntax.Between(arguments + 1, syntax.End(arguments)).TakeWhile(token => token.Text != ",").Any(token =>
                        token.IsIdentifier && (token.Text.Contains("Offset", StringComparison.Ordinal)
                            || token.Text.Contains("Allocation", StringComparison.Ordinal) || token.Text.Contains("Reserve", StringComparison.Ordinal))))
                    report("ARCH-28", syntax.Tokens[index].Offset, "List offsets come from ScreenBinningScan; atomic counters count cell coverage.");
            bool countsRectangles = syntax.HasCall("ScreenBinningCount") || syntax.HasCall("ScreenBinningCountCoverage")
                || syntax.HasCall("ScreenBinningFill");
            if (countsRectangles && !syntax.HasCall("ScreenBinningExpand"))
                report("ARCH-29", 0, "ScreenBinningExpand expands this consumer's item rectangles.");
            ReadRectangleLoops(syntax, report);
        }

        if (engine && file == "cluster_bin_reserve.slang")
        {
            if (!rasterSpillStorage)
                report("ARCH-34", 0, "RasterSpillClusters stores visible cluster indices beyond the batch capacity.");
            for (int index = 0; index + 2 < syntax.Tokens.Length; index++)
                if (syntax.Tokens[index].Text is "BinSWCount" or "BinHWCount" && syntax.Tokens[index + 1].Text == "="
                    && syntax.Tokens[index + 2].Text is "0" or "0u"
                    && syntax.Controls.Any(control => control.Keyword == "if" && control.BodyStart < index && control.BodyEnd > index
                        && syntax.Between(control.HeaderStart, control.HeaderEnd).Any(token => token.Text == ">")))
                    report("ARCH-34", syntax.Tokens[index].Offset, "Overflow clusters retain their raster work through RasterSpillClusters.");
        }
    }

    private static void ReadRectangleLoops(ShaderSyntax syntax, Action<string, int, string> report)
    {
        ShaderControl[] loops = syntax.Controls.Where(control => control.Keyword == "for").ToArray();
        bool HasRectangleBound(ShaderControl loop) => syntax.Between(loop.HeaderStart, loop.HeaderEnd).Any(token => token.IsIdentifier
            && (token.Text.Contains("Minimum", StringComparison.Ordinal) || token.Text.Contains("Maximum", StringComparison.Ordinal)
                || token.Text is "low" or "high"));
        foreach (ShaderControl outer in loops.Where(HasRectangleBound))
            if (loops.Any(inner => inner.HeaderStart > outer.BodyStart && inner.BodyEnd <= outer.BodyEnd && HasRectangleBound(inner)))
                report("ARCH-29", syntax.Tokens[outer.HeaderStart - 1].Offset, "ScreenBinningExpand assigns one thread to each covered cell or page.");
    }

    private static bool ImplementsMaterial(ShaderSyntax syntax) => syntax.Types.Any(type =>
        type.Interfaces.Contains("IMaterial") || type.Interfaces.Contains("ISurfaceEvaluate"));

    /// <summary>
    /// Material implementations are identified by actual IMaterial/ISurfaceEvaluate base types.
    /// Shipped model helpers use the explicit paths in MaterialLibraryFiles, including coating and BRDF.
    /// Engine interfaces, entries, resource declarations and terrain extraction shaders remain engine code even
    /// when their file name contains material or terrain. Sample terrain materials are consumer code
    /// under samples and enter ARCH-24 through the sample material classification.
    /// </summary>
    private static bool IsMaterialLibrary(string path, ShaderSyntax syntax) =>
        ImplementsMaterial(syntax) || MaterialLibraryFiles.Contains(path);

    private static bool IsMaterialShader(ShaderSyntax syntax) => syntax.Tokens.Any(token => token.IsIdentifier
        && (token.Text is "SurfaceEvaluate" or "TRANSPARENCY_MATERIAL_TYPE" or "CLUSTER_SURFACE_EVALUATE_TYPE"
            || token.Text.EndsWith("Material", StringComparison.Ordinal)));

    private static bool IsBinningShader(string file) => file is "clustered_light_grid.slang" or "clustered_light_binning.slang" or "material_decal_cells.slang" or "vsm_caster_invalidation.slang" or "vsm_caster_binning.slang"
        || file.StartsWith("material_decal_", StringComparison.Ordinal) && file.Contains("bin", StringComparison.Ordinal)
        || file.StartsWith("layer_", StringComparison.Ordinal) && (file.Contains("bin", StringComparison.Ordinal) || file.Contains("tile_list", StringComparison.Ordinal));

    private static string ImportLeaf(string name) =>
        (name.EndsWith(".slang", StringComparison.Ordinal) ? name[..^6] : name).Split('/', '.').Last();

    private static bool IsRetiredCompositionFile(string path)
    {
        string file = Path.GetFileName(path);
        if (path.StartsWith("assets/Shaders/", StringComparison.Ordinal))
            return file.StartsWith("cluster_transparency", StringComparison.Ordinal) || file == "material_decal_cells.slang";
        return path.StartsWith("src/SomeEngine.Render.Cluster.Pipeline/", StringComparison.Ordinal)
            && (file is "ClusterPipelineSystem.Transparency.cs" or "ClusterPipelineSystem.TransparencyShaders.cs"
                or "ClusterPipelineSystem.Decals.cs" or "ClusterPipelineSystem.Ordered.cs"
                or "ClusterPipelineSystem.BlendMeasurements.cs" or "ClusterStencil.cs"
                || file.StartsWith("ClusterBlend", StringComparison.Ordinal));
    }

    private static bool IsShadowCode(string path) => path.StartsWith("src/", StringComparison.Ordinal)
        && (path.Contains("/Shadows/", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("VirtualShadow", StringComparison.Ordinal));
    private static bool IsLayerOrDecalCode(string path) => path.StartsWith("src/SomeEngine.Render.Features/Layers/", StringComparison.Ordinal)
        || path.StartsWith("src/SomeEngine.Render.Features/Decals/", StringComparison.Ordinal);
}

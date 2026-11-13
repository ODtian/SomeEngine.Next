using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SomeEngine.ArchitectureRules;

public static class RenderArchitectureRules
{
    private static readonly HashSet<string> RenderingAssemblies =
    [
        "SomeEngine.Render", "SomeEngine.Render.Cluster", "SomeEngine.Render.Cluster.Pipeline",
        "SomeEngine.Render.Features", "SomeEngine.Render.MaterialBindless", "SomeEngine.Terrain",
        "SomeEngine.Elements.Render", "SomeEngine.Runtime",
    ];
    private static readonly HashSet<string> CompilerCalls =
        ["CreateSession", "CreateCompositeComponentType", "Link", "Specialize", "LoadModuleFromIRBlob", "LoadModuleFromSource"];
    private static readonly HashSet<string> CommandErrors =
        ["ArgumentException", "ArgumentOutOfRangeException", "ArgumentNullException", "InvalidOperationException", "ObjectDisposedException"];

    public static IReadOnlyList<ArchitectureViolation> Check(string repository, IEnumerable<string>? rules = null,
        IEnumerable<string>? paths = null)
    {
        var selected = rules?.ToHashSet(StringComparer.Ordinal);
        string[] prefixes = paths?.Select(path => path.Replace('\\', '/').TrimEnd('/')).ToArray() ?? [];
        var result = new List<ArchitectureViolation>();
        var sourceFiles = Files(Path.Combine(repository, "src"), ".cs")
            .Concat(Files(Path.Combine(repository, "samples"), ".cs"))
            .Concat(Files(Path.Combine(repository, "tools"), ".cs"));
        var lightingNames = new HashSet<string>();
        var lightingLiterals = new Dictionary<string, List<(string Path, SyntaxToken Token, SourceText Source)>>();
        var constructions = new Dictionary<string, List<(string Path, SyntaxNode Node, SourceText Source)>>();
        foreach (string file in sourceFiles)
        {
            string path = Path.GetRelativePath(repository, file).Replace('\\', '/');
            if (prefixes.Length != 0 && !prefixes.Any(prefix => path == prefix || path.StartsWith(prefix + '/'))) continue;
            string source = File.ReadAllText(file);
            SourceText text = SourceText.From(source);
            SyntaxNode root = CSharpSyntaxTree.ParseText(text).GetRoot();
            string assembly = path.Split('/').ElementAtOrDefault(1) ?? "";
            bool rendering = path.StartsWith("src/") && RenderingAssemblies.Contains(assembly);
            void Report(string rule, SyntaxNode node, string message) => Add(rule, path, text, node.SpanStart, message);
            bool compilerFile = path.StartsWith("src/SomeEngine.Resources/ShaderLibrary") ||
                path.StartsWith("src/SomeEngine.Assets/Importing/Shaders/");
            foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string name = CallName(call.Expression);
                if (!compilerFile && CompilerCalls.Contains(name) && UsesSlangCall(call, name))
                    Report("ARCH-10", call, $"Slang {name} belongs to ShaderLibrary or shader importers.");
                if (rendering && name is "CreateBuffer" or "CreateTexture" or "Map" && BackendReceiver(call.Expression))
                    Report("ARCH-04", call, $"{name} uses the RenderGraph resource path.");
                if (rendering && name is "CreateComputePipeline" or "CreateComputePipelineAsync" or "CreateGraphicsPipeline" or
                    "CreateGraphicsPipelineAsync" or "CreateMeshPipeline" or "CreateMeshPipelineAsync")
                {
                    if (!path.EndsWith("/RenderPipelines.cs"))
                        Report("ARCH-11", call, "Rendering pipeline creation belongs to RenderPipelines.");
                    else if (call.ArgumentList.Arguments.Count < 3 || call.ArgumentList.Arguments[2].Expression.IsKind(SyntaxKind.NullLiteralExpression))
                        Report("ARCH-11", call, "Pipeline creation supplies the renderer pipeline cache.");
                }
                if (rendering && name == "Resolve" && call.Expression is MemberAccessExpressionSyntax member &&
                    member.Expression.ToString().EndsWith("ShaderResourceLayout"))
                    foreach (ArgumentSyntax argument in call.ArgumentList.Arguments)
                        if (argument.Expression.DescendantNodesAndSelf().Any(node => node is SwitchExpressionSyntax or SwitchStatementSyntax))
                            Report("ARCH-21", argument, "Shader resource names come from the feature resource table.");
            }
            foreach (GenericNameSyntax generic in root.DescendantNodes().OfType<GenericNameSyntax>())
                if (generic.Identifier.ValueText == "Lazy" && generic.TypeArgumentList.Arguments.Any(type => type.ToString() == "Shader"))
                    Report("ARCH-10", generic, "Shader variants are ready before frame reconciliation.");
            foreach (MemberAccessExpressionSyntax member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                if (rendering && member.ToString() == "MapType.Read")
                    Report("ARCH-16", member, "GPU readback mapping belongs to RenderGraph.");
            if (assembly is "SomeEngine.Graphics.Direct3D12" or "SomeEngine.Graphics.Vulkan")
            {
                foreach (FieldDeclarationSyntax field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                {
                    if (field.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Contains("ThreadStatic")))
                        Report("ARCH-08", field, "Graphics recording state belongs to the backend or device instance.");
                    if (field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
                        Report("ARCH-08", field, "Mutable graphics state belongs to the backend or device instance.");
                }
                string stem = Path.GetFileNameWithoutExtension(path);
                bool commands = assembly.EndsWith("Direct3D12") ? stem.StartsWith("Command") :
                    new[] { "VulkanCommands", "VulkanRendering", "VulkanCopies", "VulkanBarriers", "VulkanIndirect" }.Contains(stem);
                if (commands)
                    foreach (ThrowStatementSyntax statement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
                        if (statement.Expression is ObjectCreationExpressionSyntax error && CommandErrors.Contains(error.Type.ToString()))
                            Report("ARCH-09", statement, "Command input checks belong to ValidationLayer.");
            }
            if (assembly == "SomeEngine.Runtime")
            {
                foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    string type = creation.Type.ToString().Split('.').Last();
                    if (type is not ("RenderGraph" or "RenderDevice" or "ClusterGeometryPipeline" or "ClusterShadingPipeline" or
                        "ClusterGraphInstaller" or "LightingFeature" or "VirtualShadowFeature")) continue;
                    if (!constructions.TryGetValue(type, out var instances)) constructions.Add(type, instances = []);
                    instances.Add((path, creation, text));
                    ConstructorDeclarationSyntax? constructor = creation.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
                    if (constructor?.Identifier.ValueText != "ApplicationRenderer")
                        Report("ARCH-12", creation, $"{type} is constructed by ApplicationRenderer.");
                }
                foreach (IdentifierNameSyntax identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (identifier.Identifier.ValueText is "ClusterGeometryBlock" or "ClusterVertexAttributesBlock")
                        Report("ARCH-13", identifier, "Scene loading consumes the baked ClusterMeshBlock.");
            }
            if (assembly == "SomeEngine.Render.Cluster.Pipeline")
            {
                foreach (TypeDeclarationSyntax type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    if (type.Modifiers.Any(SyntaxKind.PartialKeyword)) Report("ARCH-18", type, "Cluster types have one file and one responsibility.");
                Match feature = Regex.Match(source, "shadow|vsm|transparen|decal", RegexOptions.IgnoreCase);
                if (feature.Success) Add("ARCH-18", path, text, feature.Index, "Feature-specific rendering belongs to Render.Features.");
            }
            if (path.StartsWith("src/SomeEngine.Render.Features/Lighting/"))
                foreach (SyntaxToken token in root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.StringLiteralToken)))
                {
                    if (path.EndsWith("/LightingResources.cs")) lightingNames.Add(token.ValueText);
                    if (!lightingLiterals.TryGetValue(token.ValueText, out var uses)) lightingLiterals.Add(token.ValueText, uses = []);
                    uses.Add((path, token, text));
                }
        }
        foreach (var pair in constructions)
            if (pair.Value.Count != 1)
                foreach (var instance in pair.Value)
                    Add("ARCH-12", instance.Path, instance.Source, instance.Node.SpanStart, $"{pair.Key} has one construction per renderer.");
        foreach (string name in lightingNames)
            if (lightingLiterals[name].Count != 1)
                foreach (var use in lightingLiterals[name])
                    Add("ARCH-19", use.Path, use.Source, use.Token.SpanStart, $"Shader resource {name} is declared once in LightingResources.");
        string wiki = File.ReadAllText(Path.Combine(repository, "wiki/architecture/shader/Shader-Build.md"));
        HashSet<string> includes = Regex.Matches(wiki, "`([^`]+[.]slang)`").Select(match => match.Groups[1].Value).ToHashSet();
        foreach (string file in Files(Path.Combine(repository, "assets/Shaders"), ".slang"))
        {
            string path = Path.GetRelativePath(repository, file).Replace('\\', '/');
            if (prefixes.Length != 0 && !prefixes.Any(prefix => path == prefix || path.StartsWith(prefix + '/'))) continue;
            SourceText source = SourceText.From(File.ReadAllText(file));
            foreach (Match include in Regex.Matches(source.ToString(), "(?m)^\\s*#\\s*include\\s*[\"<]([^\">]+)[\">]"))
                if (!includes.Contains(include.Groups[1].Value))
                    Add("ARCH-15", path, source, include.Index, $"Include {include.Groups[1].Value} belongs in the macro header list or uses Slang import.");
        }
        return result;

        void Add(string rule, string path, SourceText source, int offset, string message)
        {
            if (selected is not null && !selected.Contains(rule)) return;
            LinePosition position = source.Lines.GetLinePosition(offset);
            result.Add(new(rule, path, position.Line + 1, position.Character + 1, message));
        }
    }

    private static string CallName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => "",
    };
    private static bool BackendReceiver(ExpressionSyntax expression) => expression is MemberAccessExpressionSyntax member &&
        (member.Expression.ToString().EndsWith("backend", StringComparison.OrdinalIgnoreCase) ||
         member.Expression.ToString().Contains("IGraphicsBackend"));
    private static bool UsesSlangCall(InvocationExpressionSyntax call, string name) =>
        name is not ("Link" or "Specialize") || call.ArgumentList.Arguments.Any(argument =>
            argument.Expression.ToString().Contains("Slang") || argument.ToString().Contains("out "));
    private static IEnumerable<string> Files(string directory, string extension)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(directory, "*" + extension)) yield return file;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "Library" or "external" || name.StartsWith('.')) continue;
            foreach (string file in Files(child, extension)) yield return file;
        }
    }
}

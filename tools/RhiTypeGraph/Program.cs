using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

#if ECS_GRAPH
const string toolName = "EcsTypeGraph";
const string graphSchema = "someengine-ecs-type-dependencies-v1";
const string documentStem = "ecs-type-dependencies";
const string graphTitle = "SomeEngine ECS";
const string markdownTitle = "SomeEngine ECS 完整类型依赖与持久状态图索引";
const string graphvizName = "SomeEngineEcsTypes";
const bool includeStateEdges = true;
#else
const string toolName = "RhiTypeGraph";
const string graphSchema = "someengine-rhi-render-graph-type-dependencies-v4";
const string documentStem = "rhi-render-graph-type-dependencies";
const string graphTitle = "RHI / Render Graph";
const string markdownTitle = "RHI / Render Graph 完整类型依赖图索引";
const string graphvizName = "RhiRenderGraphTypes";
const bool includeStateEdges = false;
#endif

if (args.Length != 1)
{
    Console.Error.WriteLine($"Usage: {toolName} <repository-root>");
    return 2;
}

string repository = Path.GetFullPath(args[0]);
string sourceRoot = Path.Combine(repository, "src");
string docsRoot = Path.Combine(repository, "docs");

MSBuildLocator.RegisterDefaults();
using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
workspace.RegisterWorkspaceFailedHandler(failure =>
    Console.Error.WriteLine($"workspace: {failure.Diagnostic.Kind}: {failure.Diagnostic.Message}"));

#if ECS_GRAPH
ProjectSpec[] specs =
[
    Product("SomeEngine.ECS"),
    Product("SomeEngine.ECS.Systems"),
    Product("SomeEngine.ECS.Serialization"),
    Product("SomeEngine.ECS.SourceGen", includeInCompiledScan: false),
];
#else
ProjectSpec[] specs =
[
    Product("SomeEngine.Graphics"),
    Product("SomeEngine.Graphics.Direct3D12"),
    Product("SomeEngine.RenderGraph"),
    Product("SomeEngine.RenderGraph.Diagnostics"),
];
#endif

var projects = new List<LoadedProject>();
foreach (ProjectSpec spec in specs)
{
    string projectPath = Path.GetFullPath(spec.ProjectPath);
    Project project = workspace.CurrentSolution.Projects.FirstOrDefault(
        candidate => string.Equals(
            candidate.FilePath is null ? null : Path.GetFullPath(candidate.FilePath),
            projectPath,
            StringComparison.OrdinalIgnoreCase))
        ?? await workspace.OpenProjectAsync(projectPath);
    Compilation compilation = await project.GetCompilationAsync()
        ?? throw new InvalidOperationException($"Could not compile {spec.ProjectPath}.");
    projects.Add(new LoadedProject(
        project,
        compilation,
        spec.Include,
        spec.IncludeInCompiledScan));
}

var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
var nodeIdByMetadataId = new Dictionary<string, string>(StringComparer.Ordinal);
var toArraySites = new List<MaterializationSite>();
var hotSerializationSites = new List<MaterializationSite>();
var collectionBoundarySites =
    new Dictionary<string, CollectionBoundarySite>(StringComparer.Ordinal);
foreach (LoadedProject loaded in projects)
{
    foreach (SyntaxTree tree in loaded.Compilation.SyntaxTrees)
    {
        if (!IncludeTree(tree, loaded.Include)) continue;
        SemanticModel model = loaded.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        SyntaxNode root = await tree.GetRootAsync();
        string treePath = RelativePath(tree.FilePath);
        foreach (MemberDeclarationSyntax declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            ISymbol? symbol = declaration switch
            {
                BaseTypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
                DelegateDeclarationSyntax @delegate => model.GetDeclaredSymbol(@delegate),
                _ => null,
            };
            if (symbol is not INamedTypeSymbol named) continue;
            string id = SymbolId(named);
            if (!nodes.TryGetValue(id, out MutableNode? node))
            {
                node = new MutableNode(
                    id,
                    named.ContainingAssembly.Name,
                    DisplayName(named),
                    named.Name,
                    named.TypeKind.ToString(),
                    named.DeclaredAccessibility.ToString(),
                    DescribeRetainedMembers(named),
                    CountDeclaredOrdinaryMethods(named),
                    CountDeclaredConstructors(named));
                nodes.Add(id, node);
                foreach (CollectionBoundarySite site in DescribeCollectionBoundaries(named, id))
                {
                    collectionBoundarySites.TryAdd(
                        $"{site.ContainingType}|{site.Member}|{site.Role}|{site.Type}",
                        site);
                }
            }
            string metadataId = MetadataSymbolId(named);
            if (nodeIdByMetadataId.TryGetValue(metadataId, out string? existingId) && existingId != id)
                throw new InvalidOperationException($"Metadata identity {metadataId} maps to both {existingId} and {id}.");
            nodeIdByMetadataId[metadataId] = id;
            if (treePath.Length != 0) node.Files.Add(treePath);
        }

#if ECS_GRAPH
        if (IsEcsHotPath(treePath))
        {
            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                    !IsSerializationWork(method))
                {
                    continue;
                }

                AddHotSerializationSite(invocation, method);
            }

            foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes()
                         .OfType<ObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor ||
                    !IsSerializationWork(constructor))
                {
                    continue;
                }

                AddHotSerializationSite(creation, constructor);
            }
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            SymbolInfo invocationInfo = model.GetSymbolInfo(invocation);
            IMethodSymbol? invokedMethod =
                invocationInfo.Symbol as IMethodSymbol ??
                invocationInfo.CandidateSymbols.OfType<IMethodSymbol>().SingleOrDefault();
            bool syntaxNamesToArray = InvocationName(invocation.Expression) == "ToArray";
            if (invokedMethod?.Name != "ToArray")
            {
                if (syntaxNamesToArray && invokedMethod is null)
                {
                    FileLinePositionSpan unresolvedLine = tree.GetLineSpan(invocation.Span);
                    throw new InvalidOperationException(
                        $"Could not resolve ToArray invocation at {treePath}:" +
                        $"{unresolvedLine.StartLinePosition.Line + 1}; " +
                        "an unresolved materialization site cannot pass the boundary gate.");
                }
                continue;
            }

            ISymbol? containingSymbol = model.GetEnclosingSymbol(invocation.SpanStart);
            INamedTypeSymbol? containingType = containingSymbol?.ContainingType;
            if (containingType is null)
                continue;
            string containingTypeId = SymbolId(containingType);
            if (!nodes.ContainsKey(containingTypeId))
                continue;

            FileLinePositionSpan lineSpan = tree.GetLineSpan(invocation.Span);
            toArraySites.Add(new MaterializationSite(
                containingTypeId,
                containingSymbol?.Name ?? "",
                treePath,
                lineSpan.StartLinePosition.Line + 1));
        }

        // An explicitly named materialization API may allocate with `new[]`, `Clone`, or another
        // mechanism and therefore has no ToArray invocation for the scan above to see. Audit the
        // API boundary itself as well, so changing its implementation cannot bypass the gate.
        foreach (MethodDeclarationSyntax method in root.DescendantNodes()
                     .OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText != "ToArray" ||
                model.GetDeclaredSymbol(method) is not IMethodSymbol
                {
                    ReturnType: IArrayTypeSymbol,
                    ContainingType: { } containingType,
                } methodSymbol)
            {
                continue;
            }

            string containingTypeId = SymbolId(containingType);
            if (!nodes.ContainsKey(containingTypeId))
                continue;

            FileLinePositionSpan lineSpan = tree.GetLineSpan(method.Identifier.Span);
            toArraySites.Add(new MaterializationSite(
                containingTypeId,
                methodSymbol.Name,
                treePath,
                lineSpan.StartLinePosition.Line + 1));
        }

        void AddHotSerializationSite(SyntaxNode syntax, IMethodSymbol operation)
        {
            ISymbol? containingSymbol = model.GetEnclosingSymbol(syntax.SpanStart);
            INamedTypeSymbol? containingType = containingSymbol?.ContainingType;
            if (containingType is null)
                return;

            string containingTypeId = SymbolId(containingType);
            if (!nodes.ContainsKey(containingTypeId))
                return;

            FileLinePositionSpan lineSpan = tree.GetLineSpan(syntax.Span);
            hotSerializationSites.Add(new MaterializationSite(
                containingTypeId,
                containingSymbol?.Name ?? operation.Name,
                treePath,
                lineSpan.StartLinePosition.Line + 1));
        }
#endif
    }
}

var explicitSyntaxEdges = new Dictionary<(string Source, string Target), HashSet<string>>();
foreach (LoadedProject loaded in projects)
{
    foreach (SyntaxTree tree in loaded.Compilation.SyntaxTrees)
    {
        if (!IncludeTree(tree, loaded.Include)) continue;
        SemanticModel model = loaded.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        SyntaxNode root = await tree.GetRootAsync();
        foreach (SimpleNameSyntax name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            MemberDeclarationSyntax? declaration = name.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .FirstOrDefault(static candidate =>
                    candidate is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
            ISymbol? sourceSymbol = declaration switch
            {
                BaseTypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
                DelegateDeclarationSyntax @delegate => model.GetDeclaredSymbol(@delegate),
                _ => null,
            };
            if (sourceSymbol is not INamedTypeSymbol sourceType) continue;
            string sourceId = SymbolId(sourceType);
            if (!nodes.ContainsKey(sourceId)) continue;

            string kind = ExplicitSyntaxReferenceKind(name, declaration!);
            SymbolInfo symbolInfo = model.GetSymbolInfo(name);
            AddExplicitSyntaxSymbol(sourceId, symbolInfo.Symbol, kind);
            foreach (ISymbol candidate in symbolInfo.CandidateSymbols)
                AddExplicitSyntaxSymbol(sourceId, candidate, kind);
        }
    }
}

var compiledCategorizedEdges = new HashSet<CategorizedEdge>();
foreach (LoadedProject loaded in projects.Where(static loaded => loaded.IncludeInCompiledScan))
{
    using var image = new MemoryStream();
    EmitResult emit = loaded.Compilation.Emit(image);
    if (!emit.Success)
    {
        string diagnostics = string.Join(
            Environment.NewLine,
            emit.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        throw new InvalidOperationException($"Could not emit {loaded.Project.Name}:{Environment.NewLine}{diagnostics}");
    }
    image.Position = 0;
    CompiledDependencyScanner.AddAssemblyEdges(image, nodeIdByMetadataId, compiledCategorizedEdges);
}

var compiledEdges = compiledCategorizedEdges
    .GroupBy(static edge => (edge.Source, edge.Target))
    .ToDictionary(
        static group => group.Key,
        static group => group.Select(static edge => edge.Kind).ToHashSet(StringComparer.Ordinal));

var compiledOnlyPairs = compiledEdges
    .Where(pair => !explicitSyntaxEdges.ContainsKey(pair.Key))
    .OrderBy(static pair => pair.Key.Source, StringComparer.Ordinal)
    .ThenBy(static pair => pair.Key.Target, StringComparer.Ordinal)
    .Select(static pair => new
    {
        source = pair.Key.Source,
        target = pair.Key.Target,
        kinds = pair.Value.Order(StringComparer.Ordinal).ToArray(),
    })
    .ToArray();
var explicitSyntaxOnlyPairs = explicitSyntaxEdges
    .Where(pair => !compiledEdges.ContainsKey(pair.Key))
    .OrderBy(static pair => pair.Key.Source, StringComparer.Ordinal)
    .ThenBy(static pair => pair.Key.Target, StringComparer.Ordinal)
    .Select(static pair => new
    {
        source = pair.Key.Source,
        target = pair.Key.Target,
        kinds = pair.Value.Order(StringComparer.Ordinal).ToArray(),
    })
    .ToArray();

var categorizedEdges = new HashSet<CategorizedEdge>();
foreach (CategorizedEdge edge in compiledCategorizedEdges)
    categorizedEdges.Add(edge with { Kind = NormalizeCompiledCategory(edge.Kind) });
foreach (KeyValuePair<(string Source, string Target), HashSet<string>> pair in explicitSyntaxEdges)
foreach (string kind in pair.Value)
    categorizedEdges.Add(new CategorizedEdge(pair.Key.Source, pair.Key.Target, kind));

var edges = categorizedEdges
    .GroupBy(static edge => (edge.Source, edge.Target))
    .ToDictionary(
        static group => group.Key,
        static group => group.Select(static edge => edge.Kind).ToHashSet(StringComparer.Ordinal));

string NormalizeCompiledCategory(string kind) => kind switch
{
    "contains" => "containment",
    "creates" => "creation",
    "implements" or "inherits" => "inheritance",
    "signature" => "signature",
    "uses" => "body-use",
    _ => throw new InvalidOperationException($"Unknown compiled edge category {kind}."),
};

string[] nodeIds = nodes.Keys.Order(StringComparer.Ordinal).ToArray();
var outgoing = nodeIds.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
var incoming = nodeIds.ToDictionary(static id => id, static _ => new HashSet<string>(StringComparer.Ordinal));
foreach ((string source, string target) in edges.Keys)
{
    outgoing[source].Add(target);
    incoming[target].Add(source);
}

List<List<string>> components = Tarjan(nodeIds, outgoing);
var componentByNode = new Dictionary<string, int>(StringComparer.Ordinal);
for (int component = 0; component < components.Count; component++)
foreach (string node in components[component])
    componentByNode[node] = component;

var componentDependencies = Enumerable.Range(0, components.Count)
    .Select(static _ => new HashSet<int>())
    .ToArray();
foreach ((string source, string target) in edges.Keys)
{
    int sourceComponent = componentByNode[source];
    int targetComponent = componentByNode[target];
    if (sourceComponent != targetComponent)
        componentDependencies[sourceComponent].Add(targetComponent);
}

var componentRanks = Enumerable.Repeat(-1, components.Count).ToArray();
int Rank(int component)
{
    if (componentRanks[component] >= 0) return componentRanks[component];
    int rank = 0;
    foreach (int dependency in componentDependencies[component])
        rank = Math.Max(rank, checked(Rank(dependency) + 1));
    componentRanks[component] = rank;
    return rank;
}
for (int component = 0; component < components.Count; component++) _ = Rank(component);

foreach (string id in nodeIds)
{
    MutableNode node = nodes[id];
    node.Rank = componentRanks[componentByNode[id]];
    node.Component = componentByNode[id];
    node.Incoming = incoming[id].Count;
    node.Outgoing = outgoing[id].Count;
}

#if ECS_GRAPH
var sharedRetainedReferenceTargets = categorizedEdges
    .Where(static edge => edge.Kind == "state")
    .GroupBy(static edge => edge.Target, StringComparer.Ordinal)
    .Select(group => new
    {
        target = group.Key,
        sources = group
            .Select(static edge => edge.Source)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray(),
    })
    .Where(static candidate => candidate.sources.Length > 1)
    .OrderByDescending(static candidate => candidate.sources.Length)
    .ThenBy(static candidate => candidate.target, StringComparer.Ordinal)
    .ToArray();
MutableNode[] singleMemberDescriptors = nodes.Values
    .Where(static node =>
        IsDescriptorNamedCandidate(node) &&
        node.RetainedMembers.Length == 1)
    .OrderBy(static node => node.FullName, StringComparer.Ordinal)
    .ToArray();
if (singleMemberDescriptors.Length != 0)
{
    throw new InvalidOperationException(
        "Descriptor-like types may not be single-member wrappers. Give the descriptor its own " +
        "normalized state/invariants or consume the canonical value directly:" +
        Environment.NewLine +
        string.Join(
            Environment.NewLine,
            singleMemberDescriptors.Select(node =>
                $"{node.FullName} -> {node.RetainedMembers[0].Name}: " +
                node.RetainedMembers[0].Type)));
}
ReviewedMaterializationSite[] reviewedToArraySites = toArraySites
    .Select(site => ReviewToArraySite(site, nodes[site.ContainingType]))
    .OrderBy(static site => site.File, StringComparer.Ordinal)
    .ThenBy(static site => site.Line)
    .ThenBy(static site => site.ContainingType, StringComparer.Ordinal)
    .ToArray();
foreach (IGrouping<(string File, string Type, string Member), ReviewedMaterializationSite> group
         in reviewedToArraySites.GroupBy(static site =>
             (site.File, site.ContainingType, site.ContainingMember)))
{
    int reviewedCount = group.First().ReviewedGroupCount;
    if (group.Count() != reviewedCount)
    {
        throw new InvalidOperationException(
            $"ToArray boundary review count changed for {group.Key.File} " +
            $"{group.Key.Type}.{group.Key.Member}: expected {reviewedCount}, found {group.Count()}. " +
            "Review the materialization boundary before updating the audit.");
    }
}
ReviewedCollectionBoundarySite[] reviewedCollectionBoundarySites =
    collectionBoundarySites.Values
        .Select(site => ReviewCollectionBoundarySite(site, nodes[site.ContainingType]))
        .OrderBy(static site => site.ContainingType, StringComparer.Ordinal)
        .ThenBy(static site => site.Member, StringComparer.Ordinal)
        .ThenBy(static site => site.Role, StringComparer.Ordinal)
        .ToArray();
if (hotSerializationSites.Count != 0)
{
    string sites = string.Join(
        Environment.NewLine,
        hotSerializationSites.Select(site =>
            $"{site.File}:{site.Line} {nodes[site.ContainingType].FullName}." +
            site.ContainingMember));
    throw new InvalidOperationException(
        "Serialization or binary-encoding work is reachable from an ECS query/mutation hot " +
        $"source file:{Environment.NewLine}{sites}");
}
#endif

var graph = new
{
    schema = graphSchema,
    generatedAt = DateTimeOffset.Now,
    source = "current working tree semantic source plus in-memory compilation",
    edgeDefinition = "One source-type to referenced-project-type pair. The authoritative graph is the union of semantic source references and compiled signature/IL references, so it retains both compile-time-erased dependencies and inferred/implicit compiled dependencies.",
    edgeCategories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["signature"] = "A declaration metadata dependency: field, property, event, parameter, return type, generic constraint, or annotation.",
        ["inheritance"] = "A base-class or implemented-interface dependency.",
        ["creation"] = "A method or initializer constructs the target type or an array whose type token resolves to it.",
        ["body-use"] = "A non-creation dependency in executable source or IL, including compile-time-erased enum/constant references and inferred compiled owner/type references.",
        ["containment"] = "The source type lexically contains the target nested type.",
        ["state"] = "The source type retains the target reference in non-static instance state. Ownership must be inferred from construction, escape, publication, replacement, and cleanup paths.",
        ["value-state"] = "The source retains the target value type inline; no independently releasable target lifetime exists.",
    },
    categorizedEdgeDefinition = "One unique source-type, target-type, category triple. Repeated references of the same category do not increase this count.",
    nodeShapeDefinition = "Declared instance field/property, ordinary-method, and constructor counts exclude compiler-synthesized members and exist only to locate review candidates; they do not decide whether a type is a wrapper.",
    nodeCount = nodes.Count,
    edgeCount = edges.Count,
    categorizedEdgeCount = categorizedEdges.Count,
    edgeCategoryCounts = categorizedEdges
        .GroupBy(static edge => edge.Kind)
        .OrderBy(static group => group.Key, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
    edgeMethodReconciliation = new
    {
        authoritativeMethod = "union of semantic source and compiled signature/IL",
        authoritativePairCount = edges.Count,
        comparedMethods = "historical v2 explicit SimpleNameSyntax scan versus recovered first-round compiled signature/IL scan",
        explicitSyntaxPairCount = explicitSyntaxEdges.Count,
        compiledPairCount = compiledEdges.Count,
        compiledCategorizedEdgeCount = compiledCategorizedEdges.Count,
        sharedPairCount = compiledEdges.Keys.Count(explicitSyntaxEdges.ContainsKey),
        compiledOnlyPairCount = compiledOnlyPairs.Length,
        explicitSyntaxOnlyPairCount = explicitSyntaxOnlyPairs.Length,
        historicalNetPairDifference = compiledOnlyPairs.Length - explicitSyntaxOnlyPairs.Length,
        explanation = includeStateEdges
            ? "The source scan contributes compile-time-erased references and retained instance-state candidates; the compiled scan contributes inferred and implicit signature/IL references. The authoritative graph is their union."
            : "The historical 2,728 versus 2,720 difference was 167 compiled-only pairs minus 159 syntax-only pairs, not eight isolated missing pairs. That audit revision used their 2,887-pair union; the current authoritative union and category totals are emitted by the adjacent generated fields.",
        compiledCategoryCounts = compiledCategorizedEdges
            .GroupBy(static edge => edge.Kind)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
        explicitSyntaxCategoryCounts = explicitSyntaxEdges.Values
            .SelectMany(static kinds => kinds)
            .GroupBy(static kind => kind)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
        compiledOnlyCategoryMemberships = compiledOnlyPairs
            .SelectMany(static pair => pair.kinds)
            .GroupBy(static kind => kind)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
        explicitSyntaxOnlyCategoryMemberships = explicitSyntaxOnlyPairs
            .SelectMany(static pair => pair.kinds)
            .GroupBy(static kind => kind)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
        compiledOnlyPairs,
        explicitSyntaxOnlyPairs,
    },
    stronglyConnectedComponentCount = components.Count,
    cyclicComponentCount = components.Count(static component => component.Count > 1),
    maximumRank = nodes.Values.Max(static node => node.Rank),
    nodes = nodes.Values
        .OrderBy(static node => node.Rank)
        .ThenBy(static node => node.Assembly, StringComparer.Ordinal)
        .ThenBy(static node => node.FullName, StringComparer.Ordinal)
        .Select(static node => new
        {
            node.Id,
            node.Assembly,
            node.FullName,
            node.Name,
            node.Kind,
            node.Accessibility,
            node.Rank,
            node.Component,
            node.Incoming,
            node.Outgoing,
            node.DeclaredDataMemberCount,
            node.DeclaredOrdinaryMethodCount,
            node.DeclaredConstructorCount,
            retainedMembers = node.RetainedMembers,
            files = node.Files.Order(StringComparer.Ordinal).ToArray(),
        }),
#if ECS_GRAPH
    audit = new
    {
        wrapperCandidateDefinition = "A concrete project type with exactly one explicitly declared instance field or auto-property. This is an exhaustive syntactic candidate set, not a semantic verdict: value objects, adapters, borrows, and real owners must be distinguished by construction, invariants, escape, and consumers.",
        wrapperNameCandidateDefinition = "A project type whose name ends in Wrapper, Box, Adapter, View, Handle, Scope, Facade, Proxy, Access, Borrow, Lease, Token, Cursor, or Enumerator. This naming audit is combined with the retained-member audit so multi-member capabilities, borrows, adapters, and lifecycle scopes cannot escape review.",
        descriptorNameCandidateDefinition = "A project type whose name ends in Descriptor, Desc, Metadata, Info, Definition, Schema, or Manifest. Every descriptor-like shape enters review even when its name does not literally use Descriptor, and graph generation rejects any such type that retains exactly one member.",
        sharedRetainedReferenceDefinition = "A project reference type retained in instance state by more than one project source type. This locates duplicate-ownership review candidates only; shared immutable identities, caches, capabilities, and non-owning references are not duplicate ownership by themselves.",
        collectionBoundaryDefinition = "Every effectively project-visible field, property, method return, and parameter whose type is an array, System.Array, or a retainable collection class/interface. Each site must be classified as private owner implementation, explicit ownership transfer/snapshot, or replaced with ref/Span/Memory borrowing; this list intentionally excludes ref structs and Memory.",
        collectionBoundarySites = reviewedCollectionBoundarySites,
        collectionBoundaryCounts = reviewedCollectionBoundarySites
            .GroupBy(static site => site.Boundary)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal),
        hotSerializationPathDefinition = "Every invocation and object creation in the SomeEngine.ECS and SomeEngine.ECS.Systems runtime assemblies is semantically checked. Calls into the SomeEngine.ECS.Serialization assembly or BinaryReader/BinaryWriter/JsonSerializer are forbidden, so serialization work cannot become reachable through a helper outside a named hot-path file.",
        hotSerializationSites,
        sharedRetainedReferenceTargets,
        wrapperCandidates = nodes.Values
            .Where(static node =>
                node.Kind is "Class" or "Struct" &&
                node.RetainedMembers.Length == 1)
            .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
            .ThenBy(static node => node.FullName, StringComparer.Ordinal)
            .Select(static node => new
            {
                node.Id,
                node.Assembly,
                node.FullName,
                node.Name,
                node.Kind,
                node.Accessibility,
                retainedMember = node.RetainedMembers[0],
                node.DeclaredOrdinaryMethodCount,
                node.DeclaredConstructorCount,
                node.Incoming,
                node.Outgoing,
                files = node.Files.Order(StringComparer.Ordinal).ToArray(),
            }),
        wrapperNameCandidates = nodes.Values
            .Where(IsWrapperNamedCandidate)
            .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
            .ThenBy(static node => node.FullName, StringComparer.Ordinal)
            .Select(static node => new
            {
                node.Id,
                node.Assembly,
                node.FullName,
                node.Name,
                node.Kind,
                node.Accessibility,
                node.RetainedMembers,
                node.DeclaredOrdinaryMethodCount,
                node.DeclaredConstructorCount,
                node.Incoming,
                node.Outgoing,
                files = node.Files.Order(StringComparer.Ordinal).ToArray(),
            }),
        descriptors = nodes.Values
            .Where(IsDescriptorNamedCandidate)
            .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
            .ThenBy(static node => node.FullName, StringComparer.Ordinal)
            .Select(static node => new
            {
                node.Id,
                node.Assembly,
                node.FullName,
                node.Kind,
                node.Accessibility,
                node.RetainedMembers,
                node.DeclaredOrdinaryMethodCount,
                node.DeclaredConstructorCount,
                node.Incoming,
                node.Outgoing,
                files = node.Files.Order(StringComparer.Ordinal).ToArray(),
            }),
        toArrayBoundaryCounts = reviewedToArraySites
            .GroupBy(static site => site.Boundary, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal),
        toArraySites = reviewedToArraySites,
    },
#endif
    edges = edges
        .OrderBy(static pair => pair.Key.Source, StringComparer.Ordinal)
        .ThenBy(static pair => pair.Key.Target, StringComparer.Ordinal)
        .Select(static pair => new
        {
            source = pair.Key.Source,
            target = pair.Key.Target,
            kinds = pair.Value.Order(StringComparer.Ordinal).ToArray(),
        }),
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
string jsonPath = Path.Combine(docsRoot, documentStem + ".json");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(graph, jsonOptions) + Environment.NewLine);

string dotPath = Path.Combine(docsRoot, documentStem + ".dot");
await File.WriteAllTextAsync(dotPath, BuildDot());

string svgPath = Path.Combine(docsRoot, documentStem + ".svg");
await File.WriteAllTextAsync(svgPath, BuildSvg());

string markdownPath = Path.Combine(docsRoot, documentStem + ".md");
await File.WriteAllTextAsync(markdownPath, BuildMarkdown());

Console.WriteLine($"nodes={nodes.Count}");
Console.WriteLine($"edges={edges.Count}");
Console.WriteLine($"categorizedEdges={categorizedEdges.Count}");
foreach (IGrouping<string, CategorizedEdge> category in categorizedEdges.GroupBy(static edge => edge.Kind).OrderBy(static group => group.Key, StringComparer.Ordinal))
    Console.WriteLine($"category.{category.Key}={category.Count()}");
Console.WriteLine($"compiledEdges={compiledEdges.Count}");
Console.WriteLine($"compiledCategorizedEdges={compiledCategorizedEdges.Count}");
Console.WriteLine($"explicitSyntaxEdges={explicitSyntaxEdges.Count}");
Console.WriteLine($"edgeMethod.shared={compiledEdges.Keys.Count(explicitSyntaxEdges.ContainsKey)}");
Console.WriteLine($"edgeMethod.compiledOnly={compiledOnlyPairs.Length}");
Console.WriteLine($"edgeMethod.explicitSyntaxOnly={explicitSyntaxOnlyPairs.Length}");
Console.WriteLine($"components={components.Count}");
Console.WriteLine($"cycles={components.Count(static component => component.Count > 1)}");
Console.WriteLine($"maxRank={nodes.Values.Max(static node => node.Rank)}");
Console.WriteLine(markdownPath);

return 0;

ProjectSpec Product(string name, bool includeInCompiledScan = true) => new(
    Path.Combine(sourceRoot, name, $"{name}.csproj"),
    static _ => true,
    includeInCompiledScan);

bool IncludeTree(SyntaxTree tree, Func<string, bool> include)
{
    string path = tree.FilePath;
    if (string.IsNullOrWhiteSpace(path)) return false;
    string fullPath = Path.GetFullPath(path);
    if (!fullPath.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return false;
    if (fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        return false;
    return include(fullPath);
}

string RelativePath(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return "";
    return Path.GetRelativePath(repository, Path.GetFullPath(path)).Replace('\\', '/');
}

RetainedMember[] DescribeRetainedMembers(INamedTypeSymbol type)
{
    var members = new Dictionary<string, RetainedMember>(StringComparer.Ordinal);
    foreach (ISymbol member in type.GetMembers().Where(member =>
                 !member.IsStatic &&
                 !member.IsImplicitlyDeclared &&
                 member.DeclaringSyntaxReferences.Length != 0 &&
                 (member is IFieldSymbol || IsAutoProperty(member))))
    {
        RetainedMember retained = member switch
        {
            IFieldSymbol field => new RetainedMember(
                field.Name,
                "field",
                field.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                field.DeclaredAccessibility.ToString(),
                field.IsReadOnly),
            IPropertySymbol property => new RetainedMember(
                property.Name,
                "auto-property",
                property.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                property.DeclaredAccessibility.ToString(),
                property.SetMethod is null || property.SetMethod.IsInitOnly),
            _ => throw new InvalidOperationException($"Unsupported retained member {member}."),
        };
        members.TryAdd(retained.Name, retained);
    }

    // Roslyn marks positional-record properties as synthesized and gives the property no direct
    // declaring syntax reference. They are still real retained state. Read the record parameter
    // list and map each positional parameter back to its generated property so a one-value record
    // cannot evade the wrapper audit merely by changing declaration syntax.
    foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
    {
        if (reference.GetSyntax() is not RecordDeclarationSyntax
            {
                ParameterList: { } parameters,
            })
        {
            continue;
        }

        foreach (ParameterSyntax parameter in parameters.Parameters)
        {
            string name = parameter.Identifier.ValueText;
            IPropertySymbol? property = type.GetMembers(name)
                .OfType<IPropertySymbol>()
                .SingleOrDefault(static candidate => !candidate.IsStatic);
            if (property is null)
            {
                throw new InvalidOperationException(
                    $"Could not resolve positional record storage {DisplayName(type)}.{name}.");
            }

            members.TryAdd(
                name,
                new RetainedMember(
                    name,
                    "positional-record-property",
                    property.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    property.DeclaredAccessibility.ToString(),
                    property.SetMethod is null || property.SetMethod.IsInitOnly));
        }
    }

    return members.Values
        .OrderBy(static member => member.Name, StringComparer.Ordinal)
        .ToArray();
}

bool IsAutoProperty(ISymbol member)
{
    if (member is not IPropertySymbol property)
        return false;

    foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
    {
        if (reference.GetSyntax() is PropertyDeclarationSyntax
            {
                ExpressionBody: null,
                AccessorList: { } accessors,
            } &&
            accessors.Accessors.All(static accessor =>
                accessor.Body is null && accessor.ExpressionBody is null))
        {
            return true;
        }
    }

    return false;
}

int CountDeclaredOrdinaryMethods(INamedTypeSymbol type) => type.GetMembers().OfType<IMethodSymbol>().Count(static method =>
    !method.IsStatic &&
    !method.IsImplicitlyDeclared &&
    method.DeclaringSyntaxReferences.Length != 0 &&
    method.MethodKind == MethodKind.Ordinary);

int CountDeclaredConstructors(INamedTypeSymbol type) => type.GetMembers().OfType<IMethodSymbol>().Count(static method =>
    !method.IsImplicitlyDeclared &&
    method.DeclaringSyntaxReferences.Length != 0 &&
    method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor);

IEnumerable<CollectionBoundarySite> DescribeCollectionBoundaries(
    INamedTypeSymbol type,
    string typeId)
{
    if (!IsEffectivelyProjectVisible(type))
        yield break;

    foreach (ISymbol member in type.GetMembers())
    {
        if (member.IsImplicitlyDeclared ||
            member.DeclaringSyntaxReferences.Length == 0 ||
            member.DeclaredAccessibility == Accessibility.Private)
        {
            continue;
        }

        switch (member)
        {
            case IFieldSymbol field when IsRetainableCollection(field.Type):
                yield return Site(field.Name, "state-field", field.Type, field);
                break;
            case IPropertySymbol property when IsRetainableCollection(property.Type):
                yield return Site(property.Name, "property-return", property.Type, property);
                break;
            case IMethodSymbol method
                when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor:
                if (method.MethodKind == MethodKind.Ordinary &&
                    IsRetainableCollection(method.ReturnType))
                {
                    yield return Site(method.Name, "return", method.ReturnType, method);
                }

                foreach (IParameterSymbol parameter in method.Parameters)
                {
                    if (IsRetainableCollection(parameter.Type))
                    {
                        yield return Site(
                            method.MethodKind == MethodKind.Constructor ? ".ctor" : method.Name,
                            $"parameter:{parameter.Name}",
                            parameter.Type,
                            method);
                    }
                }
                break;
        }
    }

    CollectionBoundarySite Site(
        string member,
        string role,
        ITypeSymbol boundaryType,
        ISymbol symbol) =>
        new(
            typeId,
            member,
            role,
            boundaryType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.DeclaredAccessibility.ToString());
}

bool IsEffectivelyProjectVisible(INamedTypeSymbol type)
{
    for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
    {
        if (current.DeclaredAccessibility == Accessibility.Private)
            return false;
    }

    return true;
}

bool IsRetainableCollection(ITypeSymbol type)
{
    if (type is IArrayTypeSymbol)
        return true;
    if (type is not INamedTypeSymbol named)
        return false;

    string namespaceName = named.ContainingNamespace.ToDisplayString();
    string metadataName = named.OriginalDefinition.MetadataName;
    string identity = namespaceName.Length == 0
        ? metadataName
        : namespaceName + "." + metadataName;
    return identity is
        "System.Array" or
        "System.Collections.IEnumerable" or
        "System.Collections.ICollection" or
        "System.Collections.IList" or
        "System.Collections.IDictionary" or
        "System.Collections.Generic.IEnumerable`1" or
        "System.Collections.Generic.ICollection`1" or
        "System.Collections.Generic.IReadOnlyCollection`1" or
        "System.Collections.Generic.IList`1" or
        "System.Collections.Generic.IReadOnlyList`1" or
        "System.Collections.Generic.IDictionary`2" or
        "System.Collections.Generic.IReadOnlyDictionary`2" or
        "System.Collections.Generic.ISet`1" or
        "System.Collections.Generic.IReadOnlySet`1" or
        "System.Collections.Generic.List`1" or
        "System.Collections.Generic.Dictionary`2" or
        "System.Collections.Generic.HashSet`1" or
        "System.Collections.Generic.Queue`1" or
        "System.Collections.Generic.Stack`1" or
        "System.Collections.Generic.LinkedList`1";
}

void AddExplicitSyntaxSymbol(string source, ISymbol? symbol, string kind)
{
    switch (symbol)
    {
        case IAliasSymbol alias:
            AddExplicitSyntaxSymbol(source, alias.Target, kind);
            break;
        case INamedTypeSymbol type:
            AddExplicitSyntaxType(source, type, kind);
            break;
    }
}

void AddExplicitSyntaxType(string source, ITypeSymbol? type, string kind)
{
    switch (type)
    {
        case null:
            return;
        case IArrayTypeSymbol array:
            AddExplicitSyntaxType(source, array.ElementType, kind);
            return;
        case IPointerTypeSymbol pointer:
            AddExplicitSyntaxType(source, pointer.PointedAtType, kind);
            return;
        case IFunctionPointerTypeSymbol functionPointer:
            AddExplicitSyntaxType(source, functionPointer.Signature.ReturnType, kind);
            foreach (IParameterSymbol parameter in functionPointer.Signature.Parameters)
                AddExplicitSyntaxType(source, parameter.Type, kind);
            return;
        case ITypeParameterSymbol:
            return;
        case INamedTypeSymbol named:
        {
            INamedTypeSymbol definition = named.OriginalDefinition;
            if (definition.ContainingAssembly is null ||
                definition.TypeKind == TypeKind.Error)
            {
                return;
            }
            string target = SymbolId(definition);
            if (source != target && nodes.ContainsKey(target))
            {
                if (!explicitSyntaxEdges.TryGetValue((source, target), out HashSet<string>? kinds))
                {
                    kinds = new HashSet<string>(StringComparer.Ordinal);
                    explicitSyntaxEdges.Add((source, target), kinds);
                }
                kinds.Add(kind == "state" && named.IsValueType ? "value-state" : kind);
            }
            foreach (ITypeSymbol argument in named.TypeArguments)
                AddExplicitSyntaxType(source, argument, kind);
            return;
        }
    }
}

string ExplicitSyntaxReferenceKind(SyntaxNode node, MemberDeclarationSyntax declaration)
{
    foreach (SyntaxNode ancestor in node.Ancestors())
    {
        if (ReferenceEquals(ancestor, declaration)) break;
        if (ancestor is AttributeSyntax) return "signature";
        if (ancestor is ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or StackAllocArrayCreationExpressionSyntax)
            return "creation";
        if (ancestor is BaseListSyntax) return "inheritance";
        if (ancestor is TypeParameterConstraintClauseSyntax) return "signature";
        if (includeStateEdges &&
            RetainedStateReferenceKind(ancestor) is { } stateKind)
        {
            return stateKind;
        }
        if (ancestor is BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)
            return "body-use";
    }
    return "signature";
}

string? RetainedStateReferenceKind(SyntaxNode node)
{
    if (node is VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field } &&
        !field.Modifiers.Any(static modifier =>
            modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
    {
        return "state";
    }

    if (node is PropertyDeclarationSyntax property &&
        property.ExpressionBody is null &&
        property.AccessorList is not null &&
        !property.Modifiers.Any(static modifier =>
            modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) &&
        property.AccessorList.Accessors.All(static accessor =>
            accessor.Body is null && accessor.ExpressionBody is null))
    {
        return "state";
    }

    return null;
}

string SymbolId(INamedTypeSymbol symbol)
{
    INamedTypeSymbol definition = symbol.OriginalDefinition;
    return $"{definition.ContainingAssembly.Name}::{DisplayName(definition)}";
}

string MetadataSymbolId(INamedTypeSymbol symbol)
{
    INamedTypeSymbol definition = symbol.OriginalDefinition;
    var names = new Stack<string>();
    for (INamedTypeSymbol? current = definition; current is not null; current = current.ContainingType)
        names.Push(current.MetadataName);
    string typeName = string.Join("+", names);
    string namespaceName = definition.ContainingNamespace.IsGlobalNamespace
        ? ""
        : definition.ContainingNamespace.ToDisplayString();
    string fullName = namespaceName.Length == 0 ? typeName : namespaceName + "." + typeName;
    return $"{definition.ContainingAssembly.Name}::{fullName}";
}

string DisplayName(INamedTypeSymbol symbol) =>
    symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

List<List<string>> Tarjan(
    IReadOnlyList<string> ids,
    IReadOnlyDictionary<string, HashSet<string>> adjacency)
{
    int nextIndex = 0;
    var indices = new Dictionary<string, int>(StringComparer.Ordinal);
    var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
    var stack = new Stack<string>();
    var onStack = new HashSet<string>(StringComparer.Ordinal);
    var result = new List<List<string>>();

    void Visit(string node)
    {
        indices[node] = nextIndex;
        lowLinks[node] = nextIndex;
        nextIndex++;
        stack.Push(node);
        onStack.Add(node);

        foreach (string target in adjacency[node])
        {
            if (!indices.ContainsKey(target))
            {
                Visit(target);
                lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
            }
            else if (onStack.Contains(target))
            {
                lowLinks[node] = Math.Min(lowLinks[node], indices[target]);
            }
        }

        if (lowLinks[node] != indices[node]) return;
        var component = new List<string>();
        while (true)
        {
            string item = stack.Pop();
            onStack.Remove(item);
            component.Add(item);
            if (item == node) break;
        }
        component.Sort(StringComparer.Ordinal);
        result.Add(component);
    }

    foreach (string id in ids)
        if (!indices.ContainsKey(id)) Visit(id);
    return result;
}

string BuildDot()
{
    string[] palette = ["#4E79A7", "#F28E2B", "#59A14F", "#E15759", "#B07AA1", "#76B7B2"];
    string[] assemblies = nodes.Values.Select(static node => node.Assembly)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var colors = assemblies.Select((assembly, index) => (assembly, palette[index % palette.Length]))
        .ToDictionary(static item => item.assembly, static item => item.Item2, StringComparer.Ordinal);
    var ids = nodeIds.Select((id, index) => (id, dot: $"n{index}"))
        .ToDictionary(static item => item.id, static item => item.dot, StringComparer.Ordinal);
    var builder = new StringBuilder();
    builder.Append("digraph ").Append(graphvizName).AppendLine(" {");
    builder.AppendLine("  graph [rankdir=LR, overlap=false, splines=true, concentrate=false, bgcolor=\"#111318\", fontcolor=\"white\"];");
    builder.AppendLine("  node [shape=box, style=\"rounded,filled\", fontname=\"Consolas\", fontsize=9, fontcolor=\"white\", color=\"#808080\"];");
    builder.AppendLine("  edge [color=\"#6d7480\", penwidth=0.7, arrowsize=0.55];");
    foreach (string id in nodeIds)
    {
        MutableNode node = nodes[id];
        builder.Append("  ").Append(ids[id])
            .Append(" [label=\"").Append(EscapeDot(node.Name)).Append("\\n")
            .Append(NodeRankLabel(node))
            .Append("\", tooltip=\"").Append(EscapeDot(node.Id)).Append("\", fillcolor=\"")
            .Append(colors[node.Assembly]).AppendLine("\"];");
    }
    foreach (KeyValuePair<(string Source, string Target), HashSet<string>> edge in edges
                 .OrderBy(static edge => edge.Key.Source, StringComparer.Ordinal)
                 .ThenBy(static edge => edge.Key.Target, StringComparer.Ordinal))
    {
        string kinds = string.Join(",", edge.Value.Order(StringComparer.Ordinal));
        string color = edge.Value.Contains("value-state") ? "#0f766e" :
            edge.Value.Contains("state") ? "#ef4444" :
            edge.Value.Contains("containment") ? "#7c3aed" :
            edge.Value.Contains("inheritance") ? "#2563eb" :
            edge.Value.Contains("creation") ? "#ea580c" :
            edge.Value.Contains("signature") ? "#64748b" : "#cbd5e1";
        string style = edge.Value.Contains("containment") ? "dotted" : "solid";
        builder.Append("  ").Append(ids[edge.Key.Source]).Append(" -> ").Append(ids[edge.Key.Target])
            .Append(" [color=\"").Append(color).Append("\", style=").Append(style)
            .Append(", tooltip=\"").Append(EscapeDot(kinds)).AppendLine("\"];");
    }
    for (int rank = 0; rank <= nodes.Values.Max(static node => node.Rank); rank++)
    {
        builder.Append("  { rank=same; ");
        foreach (string id in nodeIds.Where(id => nodes[id].Rank == rank)) builder.Append(ids[id]).Append("; ");
        builder.AppendLine("}");
    }
    builder.AppendLine("}");
    return builder.ToString();
}

string EscapeDot(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

string NodeRankLabel(MutableNode node) => $"r{node.Rank}";

string BuildSvg()
{
    const int nodeWidth = 268;
    const int nodeHeight = 18;
    const int columnWidth = 300;
    const int rowHeight = 25;
    const int left = 36;
    const int top = 132;
    const int bottom = 36;
    int maximumRank = nodes.Values.Max(static node => node.Rank);
    var rankNodes = Enumerable.Range(0, maximumRank + 1)
        .ToDictionary(
            static rank => rank,
            rank => nodes.Values.Where(node => node.Rank == rank)
                .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
                .ThenBy(static node => node.FullName, StringComparer.Ordinal)
                .ToArray());
    int width = checked(left * 2 + (maximumRank + 1) * columnWidth);
    int height = checked(top + rankNodes.Values.Max(static values => values.Length) * rowHeight + bottom);
    var positions = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
    for (int rank = 0; rank <= maximumRank; rank++)
    {
        int x = checked(left + (maximumRank - rank) * columnWidth);
        MutableNode[] values = rankNodes[rank];
        for (int index = 0; index < values.Length; index++)
            positions[values[index].Id] = (x, checked(top + index * rowHeight));
    }

    string[] palette = ["#4263a3", "#5f3b8f", "#287a78", "#9a5b2f", "#5f6d34", "#8a3f61"];
    string[] assemblies = nodes.Values.Select(static node => node.Assembly).Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).ToArray();
    var colors = assemblies.Select((assembly, index) => (assembly, palette[index % palette.Length]))
        .ToDictionary(static item => item.assembly, static item => item.Item2, StringComparer.Ordinal);

    var builder = new StringBuilder();
    builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(width)
        .Append("\" height=\"").Append(height).Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
        .AppendLine("\" role=\"img\" aria-labelledby=\"title description\">");
    builder.Append("<title id=\"title\">").Append(EscapeXml(graphTitle))
        .AppendLine(" complete type dependency graph</title>");
    builder.Append("<desc id=\"description\">").Append(nodes.Count).Append(" source type nodes and ")
        .Append(edges.Count).AppendLine(" deduplicated compiled type-reference pairs. Arrows point from a source type to a referenced type.</desc>");
    builder.AppendLine("<defs><marker id=\"arrow\" markerWidth=\"6\" markerHeight=\"6\" refX=\"5\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 Z\" fill=\"#77808f\"/></marker></defs>");
    builder.AppendLine("<style>text{font-family:Consolas,'Cascadia Mono',monospace}.edge{fill:none;stroke-width:.65;marker-end:url(#arrow)}.edge.value-state{stroke:#2dd4bf;opacity:.18}.edge.state{stroke:#ef4444;opacity:.22}.edge.signature{stroke:#94a3b8;opacity:.13}.edge.inheritance{stroke:#60a5fa;opacity:.24}.edge.creation{stroke:#fb923c;opacity:.22}.edge.body-use{stroke:#64748b;opacity:.10}.edge.containment{stroke:#a78bfa;opacity:.38;stroke-dasharray:3 2}.node rect{stroke:#9ca3af;stroke-width:.45}.node text{font-size:7.6px;fill:#fff}.node:hover rect{stroke:#fff;stroke-width:1.5}.heading{fill:#e8eaf0;font-weight:700}.subheading{fill:#b8bec9}.legend{fill:#d9dde5;font-size:9px}</style>");
    builder.Append("<rect width=\"100%\" height=\"100%\" fill=\"#111318\"/><text x=\"").Append(left)
        .Append("\" y=\"28\" class=\"heading\" font-size=\"17\">").Append(EscapeXml(graphTitle))
        .AppendLine(" · complete type dependency graph</text>");
    builder.Append("<text x=\"").Append(left)
        .Append("\" y=\"48\" class=\"subheading\" font-size=\"10\">source type → semantic ∪ compiled project type · ")
        .Append(nodes.Count.ToString("N0")).Append(" nodes · ").Append(edges.Count.ToString("N0"))
        .Append(" pairs · ").Append(categorizedEdges.Count.ToString("N0")).Append(" categorized edges · rank ")
        .Append(maximumRank).AppendLine(" → 0</text>");

    int legendX = left;
    foreach (string assembly in assemblies)
    {
        builder.Append("<rect x=\"").Append(legendX).Append("\" y=\"61\" width=\"10\" height=\"10\" rx=\"2\" fill=\"")
            .Append(colors[assembly]).Append("\"/><text x=\"").Append(legendX + 15).Append("\" y=\"70\" class=\"legend\">")
            .Append(EscapeXml(AssemblyLabel(assembly))).AppendLine("</text>");
        legendX += checked(28 + AssemblyLabel(assembly).Length * 7);
    }

    (string Kind, string Color)[] relationshipLegend = includeStateEdges
        ?
        [
            ("value-state", "#2dd4bf"),
            ("state", "#ef4444"),
            ("signature", "#94a3b8"),
            ("inheritance", "#60a5fa"),
            ("creation", "#fb923c"),
            ("body-use", "#64748b"),
            ("containment", "#a78bfa"),
        ]
        :
        [
            ("signature", "#94a3b8"),
            ("inheritance", "#60a5fa"),
            ("creation", "#fb923c"),
            ("body-use", "#64748b"),
            ("containment", "#a78bfa"),
        ];
    int relationshipLegendX = left;
    foreach ((string kind, string color) in relationshipLegend)
    {
        builder.Append("<line x1=\"").Append(relationshipLegendX).Append("\" y1=\"84\" x2=\"")
            .Append(relationshipLegendX + 18).Append("\" y2=\"84\" stroke=\"").Append(color)
            .Append("\" stroke-width=\"2\"/><text x=\"").Append(relationshipLegendX + 23)
            .Append("\" y=\"87\" class=\"legend\">").Append(kind).AppendLine("</text>");
        relationshipLegendX += checked(42 + kind.Length * 7);
    }

    for (int rank = maximumRank; rank >= 0; rank--)
    {
        int x = checked(left + (maximumRank - rank) * columnWidth);
        builder.Append("<text x=\"").Append(x).Append("\" y=\"114\" class=\"heading\" font-size=\"11\">rank ")
            .Append(rank).Append(" · ").Append(rankNodes[rank].Length).AppendLine("</text>");
    }

    foreach (KeyValuePair<(string Source, string Target), HashSet<string>> edge in edges
                 .OrderBy(static edge => edge.Key.Source, StringComparer.Ordinal)
                 .ThenBy(static edge => edge.Key.Target, StringComparer.Ordinal))
    {
        (string source, string target) = edge.Key;
        string primaryKind = edge.Value.Contains("value-state") ? "value-state" :
            edge.Value.Contains("state") ? "state" :
            edge.Value.Contains("containment") ? "containment" :
            edge.Value.Contains("inheritance") ? "inheritance" :
            edge.Value.Contains("creation") ? "creation" :
            edge.Value.Contains("signature") ? "signature" : "body-use";
        string kinds = string.Join(",", edge.Value.Order(StringComparer.Ordinal));
        (int sourceX, int sourceY) = positions[source];
        (int targetX, int targetY) = positions[target];
        int x1 = checked(sourceX + nodeWidth);
        int y1 = checked(sourceY + nodeHeight / 2);
        int x2 = targetX;
        int y2 = checked(targetY + nodeHeight / 2);
        builder.Append("<path class=\"edge ").Append(primaryKind).Append("\" d=\"M")
            .Append(x1).Append(',').Append(y1);
        if (sourceX == targetX)
        {
            int bend = checked(x1 + 18);
            builder.Append(" C").Append(bend).Append(',').Append(y1).Append(' ').Append(bend).Append(',').Append(y2)
                .Append(' ').Append(x1).Append(',').Append(y2);
        }
        else
        {
            int middle = checked((x1 + x2) / 2);
            builder.Append(" C").Append(middle).Append(',').Append(y1).Append(' ').Append(middle).Append(',').Append(y2)
                .Append(' ').Append(x2).Append(',').Append(y2);
        }
        builder.Append("\"><title>").Append(EscapeXml(source)).Append(" → ").Append(EscapeXml(target))
            .Append(" [").Append(EscapeXml(kinds)).AppendLine("]</title></path>");
    }

    foreach (MutableNode node in nodes.Values.OrderBy(static node => node.Rank).ThenBy(static node => node.FullName, StringComparer.Ordinal))
    {
        (int x, int y) = positions[node.Id];
        string label = NodeLabel(node);
        builder.Append("<g class=\"node\"><title>").Append(EscapeXml(node.FullName)).Append(" · rank ").Append(node.Rank)
            .Append(" · in ").Append(node.Incoming).Append(" · out ").Append(node.Outgoing).Append("</title><rect x=\"")
            .Append(x).Append("\" y=\"").Append(y).Append("\" width=\"").Append(nodeWidth).Append("\" height=\"")
            .Append(nodeHeight).Append("\" rx=\"3\" fill=\"").Append(colors[node.Assembly]).Append("\"/><text x=\"")
            .Append(x + 5).Append("\" y=\"").Append(y + 12).Append("\">").Append(EscapeXml(label)).AppendLine("</text></g>");
    }
    builder.AppendLine("</svg>");
    return builder.ToString();
}

string AssemblyLabel(string assembly) => assembly switch
{
    "SomeEngine.ECS" => "ECS",
    "SomeEngine.ECS.Systems" => "Systems",
    "SomeEngine.ECS.Serialization" => "Serialization",
    "SomeEngine.ECS.SourceGen" => "SourceGen",
    "SomeEngine.Generators" => "Generator",
    "SomeEngine.Graphics" => "RHI",
    "SomeEngine.Graphics.Direct3D12" => "D3D12",
    "SomeEngine.RenderGraph" => "RG",
    "SomeEngine.RenderGraph.Diagnostics" => "Diagnostics",
    _ => assembly,
};

string NodeLabel(MutableNode node)
{
    string prefix = AssemblyLabel(node.Assembly);
    string local = node.FullName[(node.Assembly.Length + 1)..];
    string value = $"{prefix}/{local}";
    return value.Length <= 43 ? value : value[..42] + "…";
}

string EscapeXml(string value) => value
    .Replace("&", "&amp;", StringComparison.Ordinal)
    .Replace("<", "&lt;", StringComparison.Ordinal)
    .Replace(">", "&gt;", StringComparison.Ordinal)
    .Replace("\"", "&quot;", StringComparison.Ordinal)
    .Replace("'", "&apos;", StringComparison.Ordinal);

string BuildMarkdown()
{
    var builder = new StringBuilder();
    builder.Append("# ").AppendLine(markdownTitle);
    builder.AppendLine();
    builder.Append("本文件同时读取当前工作树的 Roslyn semantic source 和内存编译结果。边方向为 `source type → referenced type`；权威边集取两者并集，因此既保留会在编译时擦除的 enum/constant 等语义依赖，也保留源码没有显式写出但在签名或 IL 中出现的推断/隐式依赖。每个源/目标类型对只保留一次，同时保存 `signature`、`inheritance`、`creation`、`body-use`、`containment`");
#if ECS_GRAPH
    builder.Append("、`value-state`、`state`");
#endif
    builder.AppendLine(" 类明确关系。`value-state` 是内联值，没有独立释放生命周期；引用类型 `state` 只表示持久保存，所有权必须继续根据构造、逃逸、发布、替换与清理路径推导。rank 先压缩强连通分量，再按依赖叶子计算，仅用于依赖顺序和审计，不是人为架构层级。");
    builder.AppendLine();
    builder.AppendLine($"- 节点：{nodes.Count}");
    builder.AppendLine($"- 去重依赖对：{edges.Count}");
    builder.AppendLine($"- 带类别边：{categorizedEdges.Count}");
    foreach (IGrouping<string, CategorizedEdge> category in categorizedEdges.GroupBy(static edge => edge.Kind).OrderBy(static group => group.Key, StringComparer.Ordinal))
        builder.AppendLine($"  - `{category.Key}`：{category.Count()}");
    builder.AppendLine($"- 强连通分量：{components.Count}");
    builder.AppendLine($"- 多节点强连通分量：{components.Count(static component => component.Count > 1)}");
    builder.AppendLine($"- 最大 rank：{nodes.Values.Max(static node => node.Rank)}");
    builder.Append("- 完整数据：[`").Append(documentStem).Append(".json`](")
        .Append(documentStem).AppendLine(".json)");
    builder.Append("- Graphviz：[`").Append(documentStem).Append(".dot`](")
        .Append(documentStem).AppendLine(".dot)");
    builder.Append("- 可直接打开的完整图：[`").Append(documentStem).Append(".svg`](")
        .Append(documentStem).AppendLine(".svg)");
#if ECS_GRAPH
    builder.AppendLine("- 重新生成：`dotnet run --project tools/RhiTypeGraph/RhiTypeGraph.csproj -p:DefineConstants=ECS_GRAPH -- <repository-root>`");
    builder.AppendLine("- 所有权判断台账：[`ecs-ownership-audit.md`](ecs-ownership-audit.md)");
#else
    builder.Append("- 重新生成：`dotnet run --project tools/").Append(toolName).Append('/')
        .Append(toolName).AppendLine(".csproj -- <repository-root>`");
#endif
#if !ECS_GRAPH
    builder.AppendLine("- PNG 预览：[`rhi-render-graph-type-dependencies.png`](rhi-render-graph-type-dependencies.png)");
    builder.AppendLine("- 判断台账：[`rhi-render-graph-concept-audit.md`](rhi-render-graph-concept-audit.md)");
#endif
    builder.AppendLine();
    builder.AppendLine("## 边口径对账");
    builder.AppendLine();
    builder.AppendLine(includeStateEdges
        ? "源码扫描覆盖显式语义依赖和实例字段 `state` 候选；编译扫描覆盖签名与 IL 中推断或隐式出现的依赖。两者各自可见范围不同，因此当前权威图取并集；完整逐对差异保存在 JSON 的 `edgeMethodReconciliation` 中。"
        : "上一版 `2,720` 来自源码 `SimpleNameSyntax` 显式名称扫描；第一轮 `2,728` 来自编译后签名和 IL。二者不是相差八条孤立边，而是两组较大的差异相抵后的净值。两者各自漏掉另一方能看到的真实依赖，因此当前权威图取并集；完整逐对差异保存在 JSON 的 `edgeMethodReconciliation` 中。");
    builder.AppendLine();
    builder.AppendLine("| 集合 | 类型对 |");
    builder.AppendLine("| --- | ---: |");
    builder.AppendLine($"| 两种方法共有 | {compiledEdges.Keys.Count(explicitSyntaxEdges.ContainsKey)} |");
    builder.AppendLine($"| 仅编译图有 | {compiledOnlyPairs.Length} |");
    builder.AppendLine($"| 仅显式语法图有 | {explicitSyntaxOnlyPairs.Length} |");
    builder.AppendLine($"| 历史净差 | {compiledOnlyPairs.Length - explicitSyntaxOnlyPairs.Length:+#;-#;0} |");
    builder.AppendLine($"| 当前并集 | {edges.Count} |");
    builder.AppendLine();
    builder.AppendLine("仅编译图的类别成员数（同一类型对可属于多个类别）：");
    builder.AppendLine();
    foreach (IGrouping<string, string> category in compiledOnlyPairs.SelectMany(static pair => pair.kinds).GroupBy(static kind => kind).OrderBy(static group => group.Key, StringComparer.Ordinal))
        builder.AppendLine($"- `{category.Key}`：{category.Count()}");
    builder.AppendLine();
#if ECS_GRAPH
    MutableNode[] wrapperCandidates = nodes.Values
        .Where(static node =>
            node.Kind is "Class" or "Struct" &&
            node.RetainedMembers.Length == 1)
        .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
        .ThenBy(static node => node.FullName, StringComparer.Ordinal)
        .ToArray();
    MutableNode[] descriptors = nodes.Values
        .Where(IsDescriptorNamedCandidate)
        .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
        .ThenBy(static node => node.FullName, StringComparer.Ordinal)
        .ToArray();
    MutableNode[] wrapperNameCandidates = nodes.Values
        .Where(IsWrapperNamedCandidate)
        .OrderBy(static node => node.Assembly, StringComparer.Ordinal)
        .ThenBy(static node => node.FullName, StringComparer.Ordinal)
        .ToArray();
    ReviewedCollectionBoundarySite[] collectionBoundaries =
        reviewedCollectionBoundarySites;

    builder.AppendLine("## 包装、描述符与物化边界审计");
    builder.AppendLine();
    builder.AppendLine(
        "下表是穷尽式语法候选，不把“只保留一个成员”直接等同于错误包装。命令、强类型身份、内联存储、迭代器、适配器和生命周期 scope 仍须根据构造、逃逸、不变量与释放路径判断。完整机器可读记录位于 JSON 的 `audit`。");
    builder.AppendLine();
    builder.AppendLine($"### 单成员包装候选（{wrapperCandidates.Length}）");
    builder.AppendLine();
    builder.AppendLine("| 类型 | 保留成员 | 普通方法 | 构造器 | 源文件 |");
    builder.AppendLine("| --- | --- | ---: | ---: | --- |");
    foreach (MutableNode node in wrapperCandidates)
    {
        RetainedMember member = node.RetainedMembers[0];
        builder.Append("| `").Append(node.FullName).Append("` | `")
            .Append(member.Name).Append(": ").Append(member.Type).Append("` | ")
            .Append(node.DeclaredOrdinaryMethodCount).Append(" | ")
            .Append(node.DeclaredConstructorCount).Append(" | ")
            .Append(string.Join("<br>", node.Files.Select(static file => $"`{file}`")))
            .AppendLine(" |");
    }
    builder.AppendLine();
    builder.AppendLine($"### 包装命名候选（{wrapperNameCandidates.Length}）");
    builder.AppendLine();
    builder.AppendLine(
        "名称后缀为 `Wrapper / Box / Adapter / View / Handle / Scope / Facade / Proxy / Access / Borrow / Lease / Token / Cursor / Enumerator` 的类型也全部进入审计，避免带额外 token、generation 或 capability 字段的多成员包装逃过单成员扫描。");
    builder.AppendLine();
    builder.AppendLine("| 类型 | 持久成员 | 普通方法 | 构造器 | 源文件 |");
    builder.AppendLine("| --- | ---: | ---: | ---: | --- |");
    foreach (MutableNode node in wrapperNameCandidates)
    {
        builder.Append("| `").Append(node.FullName).Append("` | ")
            .Append(node.RetainedMembers.Length).Append(" | ")
            .Append(node.DeclaredOrdinaryMethodCount).Append(" | ")
            .Append(node.DeclaredConstructorCount).Append(" | ")
            .Append(string.Join("<br>", node.Files.Select(static file => $"`{file}`")))
            .AppendLine(" |");
    }
    builder.AppendLine();
    builder.AppendLine($"### 描述符命名候选（{descriptors.Length}）");
    builder.AppendLine();
    builder.AppendLine(
        "后缀 `Descriptor / Desc / Metadata / Info / Definition / Schema / Manifest` 全部进入审计；名称只决定候选集合，不替代语义判断。");
    builder.AppendLine();
    builder.AppendLine("| 类型 | 持久成员 | 普通方法 | 构造器 | 源文件 |");
    builder.AppendLine("| --- | ---: | ---: | ---: | --- |");
    foreach (MutableNode node in descriptors)
    {
        builder.Append("| `").Append(node.FullName).Append("` | ")
            .Append(node.RetainedMembers.Length).Append(" | ")
            .Append(node.DeclaredOrdinaryMethodCount).Append(" | ")
            .Append(node.DeclaredConstructorCount).Append(" | ")
            .Append(string.Join("<br>", node.Files.Select(static file => $"`{file}`")))
            .AppendLine(" |");
    }
    builder.AppendLine();
    builder.AppendLine($"### 可保留数组/集合边界候选（{collectionBoundaries.Length}）");
    builder.AppendLine();
    builder.AppendLine(
        "表中只列有效可见的数组或可保留集合签名；`ref`、`Span<T>`、`ReadOnlySpan<T>`、`Memory<T>` 和 `ReadOnlyMemory<T>` 不在候选中。候选必须是显式所有权转移/快照，不能伪装成同步借用。");
    builder.AppendLine();
    builder.AppendLine("| 类型 | 成员 | 角色 | 边界类型 | 判定 | 可见性 |");
    builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
    foreach (ReviewedCollectionBoundarySite site in collectionBoundaries)
    {
        builder.Append("| `").Append(nodes[site.ContainingType].FullName).Append("` | `")
            .Append(site.Member).Append("` | `")
            .Append(site.Role).Append("` | `")
            .Append(site.Type).Append("` | `")
            .Append(site.Boundary).Append("` | `")
            .Append(site.Accessibility).AppendLine("` |");
    }
    builder.AppendLine();
    builder.AppendLine(
        "多来源 `state` 仅是重复所有权候选：引用类型可能被多个对象合法共享；只有构造、替换、发布和清理路径同时声称生命周期时才是重复 owner。");
    builder.AppendLine();
    builder.AppendLine($"### 多来源持久引用目标（{sharedRetainedReferenceTargets.Length}）");
    builder.AppendLine();
    builder.AppendLine("| 被保留类型 | 来源数 | 来源类型 |");
    builder.AppendLine("| --- | ---: | --- |");
    foreach (var candidate in sharedRetainedReferenceTargets)
    {
        builder.Append("| `").Append(nodes[candidate.target].FullName).Append("` | ")
            .Append(candidate.sources.Length).Append(" | ")
            .Append(string.Join("<br>", candidate.sources.Select(source => $"`{nodes[source].FullName}`")))
            .AppendLine(" |");
    }
    builder.AppendLine();
    builder.AppendLine(
        "`ToArray` 记录用于边界复核：允许位置是 owner 构造、显式 Snapshot/发布、序列化边界或异步任务所有权转移；查询、逐实体修改与写入热路径不得物化。");
    builder.AppendLine();
    builder.AppendLine($"### `ToArray` 位置（{toArraySites.Count}）");
    builder.AppendLine();
    builder.AppendLine("| 类型 | 成员 | 已审边界 | 位置 |");
    builder.AppendLine("| --- | --- | --- | --- |");
    foreach (ReviewedMaterializationSite site in reviewedToArraySites)
    {
        builder.Append("| `").Append(site.ContainingType).Append("` | `")
            .Append(site.ContainingMember).Append("` | `")
            .Append(site.Boundary).Append("` | `")
            .Append(site.File).Append(':').Append(site.Line).AppendLine("` |");
    }
    builder.AppendLine();
#endif
    builder.AppendLine("## 程序集统计");
    builder.AppendLine();
    builder.AppendLine("| 程序集 | 节点 | 跨程序集出边 | 跨程序集入边 |");
    builder.AppendLine("| --- | ---: | ---: | ---: |");
    foreach (IGrouping<string, MutableNode> assembly in nodes.Values.GroupBy(static node => node.Assembly).OrderBy(static group => group.Key, StringComparer.Ordinal))
    {
        HashSet<string> assemblyIds = assembly.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        int crossOut = edges.Keys.Count(edge => assemblyIds.Contains(edge.Source) && !assemblyIds.Contains(edge.Target));
        int crossIn = edges.Keys.Count(edge => !assemblyIds.Contains(edge.Source) && assemblyIds.Contains(edge.Target));
        builder.Append("| `").Append(assembly.Key).Append("` | ").Append(assembly.Count()).Append(" | ")
            .Append(crossOut).Append(" | ").Append(crossIn).AppendLine(" |");
    }
    builder.AppendLine();
    builder.AppendLine("## Rank 统计");
    builder.AppendLine();
    builder.AppendLine("| Rank | 节点 |");
    builder.AppendLine("| ---: | ---: |");
    foreach (IGrouping<int, MutableNode> rank in nodes.Values.GroupBy(static node => node.Rank).OrderBy(static group => group.Key))
        builder.Append("| ").Append(rank.Key).Append(" | ").Append(rank.Count()).AppendLine(" |");
    builder.AppendLine();
    builder.AppendLine("## 全部节点");
    for (int rank = 0; rank <= nodes.Values.Max(static node => node.Rank); rank++)
    {
        builder.AppendLine();
        builder.Append("### Rank ").AppendLine(rank.ToString());
        builder.AppendLine();
        builder.AppendLine("| 节点 | 程序集 | 入度 | 出度 | 源文件 |");
        builder.AppendLine("| --- | --- | ---: | ---: | --- |");
        foreach (MutableNode node in nodes.Values.Where(node => node.Rank == rank)
                     .OrderByDescending(static node => node.Incoming)
                     .ThenBy(static node => node.Assembly, StringComparer.Ordinal)
                     .ThenBy(static node => node.FullName, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(node.FullName).Append("` | `").Append(node.Assembly).Append("` | ")
                .Append(node.Incoming).Append(" | ").Append(node.Outgoing).Append(" | ")
                .Append(string.Join("<br>", node.Files.Select(static file => $"`{file}`"))).AppendLine(" |");
        }
    }
    builder.AppendLine();
    builder.AppendLine("## 多节点强连通分量");
    builder.AppendLine();
    foreach ((List<string> component, int index) in components.Select((component, index) => (component, index)).Where(static item => item.component.Count > 1))
    {
        builder.Append("- SCC ").Append(index).Append(": ")
            .AppendLine(string.Join(", ", component.Select(id => $"`{nodes[id].FullName}`")));
    }
    return builder.ToString();
}

#if ECS_GRAPH
static bool IsWrapperNamedCandidate(MutableNode node) =>
    node.Name.EndsWith("Wrapper", StringComparison.Ordinal) ||
    node.Name.EndsWith("Box", StringComparison.Ordinal) ||
    node.Name.EndsWith("Adapter", StringComparison.Ordinal) ||
    node.Name.EndsWith("View", StringComparison.Ordinal) ||
    node.Name.EndsWith("Handle", StringComparison.Ordinal) ||
    node.Name.EndsWith("Scope", StringComparison.Ordinal) ||
    node.Name.EndsWith("Facade", StringComparison.Ordinal) ||
    node.Name.EndsWith("Proxy", StringComparison.Ordinal) ||
    node.Name.EndsWith("Access", StringComparison.Ordinal) ||
    node.Name.EndsWith("Borrow", StringComparison.Ordinal) ||
    node.Name.EndsWith("Lease", StringComparison.Ordinal) ||
    node.Name.EndsWith("Token", StringComparison.Ordinal) ||
    node.Name.EndsWith("Cursor", StringComparison.Ordinal) ||
    node.Name.EndsWith("Enumerator", StringComparison.Ordinal);

static bool IsDescriptorNamedCandidate(MutableNode node) =>
    node.Name.EndsWith("Descriptor", StringComparison.Ordinal) ||
    node.Name.EndsWith("Desc", StringComparison.Ordinal) ||
    node.Name.EndsWith("Metadata", StringComparison.Ordinal) ||
    node.Name.EndsWith("Info", StringComparison.Ordinal) ||
    node.Name.EndsWith("Definition", StringComparison.Ordinal) ||
    node.Name.EndsWith("Schema", StringComparison.Ordinal) ||
    node.Name.EndsWith("Manifest", StringComparison.Ordinal);

static string? InvocationName(ExpressionSyntax expression) => expression switch
{
    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
    MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
    GenericNameSyntax generic => generic.Identifier.ValueText,
    _ => null,
};

static bool IsEcsHotPath(string file) =>
    file.StartsWith("src/SomeEngine.ECS/", StringComparison.Ordinal) ||
    file.StartsWith("src/SomeEngine.ECS.Systems/", StringComparison.Ordinal);

static bool IsSerializationWork(IMethodSymbol method)
{
    INamedTypeSymbol? type = method.ContainingType;
    if (type is null)
        return false;

    if (type.ContainingAssembly.Name == "SomeEngine.ECS.Serialization")
        return true;

    string fullName = type.ToDisplayString();
    return fullName is
        "System.IO.BinaryReader" or
        "System.IO.BinaryWriter" or
        "System.Text.Json.JsonSerializer";
}

static ReviewedCollectionBoundarySite ReviewCollectionBoundarySite(
    CollectionBoundarySite site,
    MutableNode node)
{
    string boundary = (site.Role, node.FullName, site.Member) switch
    {
        (string role, _, _) when role.StartsWith(
            "parameter:owned",
            StringComparison.Ordinal) =>
            "ownership-transfer",
        ("return", "SomeEngine.ECS.Archetypes.ArchetypeRegistry",
            "InsertSorted" or "RemoveSorted" or "SharedMap") =>
            "owner-construction",
        ("return", "SomeEngine.ECS.Archetypes.Chunk", "CaptureComponentValue") =>
            "one-row-owner-snapshot",
        ("return", "SomeEngine.ECS.Hierarchy.HierarchyChildrenSnapshot<TDomain>", "ToArray") =>
            "explicit-owner-copy",
        ("return", "SomeEngine.ECS.Relations.RelationEdgeQuery<T>", "ToArray") =>
            "explicit-owner-copy",
        ("return", "SomeEngine.ECS.Relations.RelationEntityMap<TValue>", "ToEntityArray") =>
            "stable-snapshot",
        ("return", "SomeEngine.ECS.Relations.RelationGeneration<T>",
            "OrderedShardKeysStable") =>
            "stable-mutation-plan",
        ("return", "SomeEngine.ECS.Relations.RelationTypeSlotTable", "SnapshotValues") =>
            "stable-snapshot",
        ("return", "SomeEngine.ECS.Relations.RelationTypeState<T>",
            "CommandBatchEdgesAt" or "CommandBatchEdgesBetween" or "DirtyEdgesStable") =>
            "stable-mutation-plan",
        ("parameter:array", "SomeEngine.ECS.Collections.ArrayGrowthExtensions",
            "EnsureCapacity") =>
            "owner-growth-by-ref",
        ("parameter:destination",
            "SomeEngine.ECS.Serialization.SparseSerializationPresence",
            "AddPresentRuntimesTo") =>
            "serialization-destination",
        _ => throw new InvalidOperationException(
            $"Unreviewed retainable collection boundary: {node.FullName}.{site.Member} " +
            $"{site.Role} {site.Type}. Borrowed data must use ref/Span/Memory; arrays and " +
            "retainable collections require an explicit owner-transfer or snapshot verdict."),
    };

    return new ReviewedCollectionBoundarySite(
        site.ContainingType,
        site.Member,
        site.Role,
        site.Type,
        site.Accessibility,
        boundary);
}

static ReviewedMaterializationSite ReviewToArraySite(
    MaterializationSite site,
    MutableNode node)
{
    (string boundary, int reviewedGroupCount) = (
        site.File,
        node.FullName,
        site.ContainingMember) switch
    {
        ("src/SomeEngine.ECS/Archetypes/Archetype.cs",
            "SomeEngine.ECS.Archetypes.Archetype", ".ctor") =>
            ("owner-construction", 7),
        ("src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs",
            "SomeEngine.ECS.Archetypes.SharedComponentTuple", ".ctor") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS/BundleSpawnMap.cs",
            "SomeEngine.ECS.BundleSpawnMap", ".ctor") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS/Indexing/ComponentIndex.cs",
            "SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Bucket", "Publish") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Owners.Copy.cs",
            "SomeEngine.ECS.Owners.Copy.CopyShape", "CopyIds") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "SetOrderPolicy") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "PrepareMaintenance") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "BeginTerminalDestroy") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "RollbackPreimages") =>
            ("rollback-snapshot", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "CollectCandidates") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>", "StableEntities") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.UnorderedChildShard", ".ctor") =>
            ("cow-clone", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.UnorderedChildShard", "PublishSnapshot") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard", ".ctor") =>
            ("cow-clone", 1),
        ("src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            "SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard", "PublishSnapshot") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs",
            "SomeEngine.ECS.Hierarchy.HierarchyChildrenSnapshot<TDomain>", "ToArray") =>
            ("explicit-owner-copy", 1),
        ("src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs",
            "SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>", "Rollback") =>
            ("rollback-snapshot", 1),
        ("src/SomeEngine.ECS/Queries/QueryDefinition.cs",
            "SomeEngine.ECS.Queries.QueryDefinition", ".ctor") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS/Queries/QueryDefinition.cs",
            "SomeEngine.ECS.Queries.QueryDefinition", "CreateNormalized") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS/Queries/QueryDefinition.cs",
            "SomeEngine.ECS.Queries.QueryDefinition", "CompileJobStorageAccesses") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS/Queries/QueryState.cs",
            "SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder", "TryCreate") =>
            ("owner-construction", 5),
        ("src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs",
            "SomeEngine.ECS.Relations.RelationGeneration<T>", "SetOrderPolicy") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs",
            "SomeEngine.ECS.Relations.RelationGeneration<T>", "Reorder") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Relations/RelationAdjacency.cs",
            "SomeEngine.ECS.Relations.RelationEdgeQuery<T>", "ToArray") =>
            ("explicit-owner-copy", 1),
        ("src/SomeEngine.ECS/Relations/RelationTypeState.Queries.cs",
            "SomeEngine.ECS.Relations.RelationTypeState<T>", "StableAffected") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs",
            "SomeEngine.ECS.Relations.MutableRelationAdjacencyShard<T>", "Freeze") =>
            ("cow-publication", 1),
        ("src/SomeEngine.ECS/Relations/RelationTypeState.Tracking.cs",
            "SomeEngine.ECS.Relations.RelationTypeState<T>", "StableLiveEdges") =>
            ("stable-mutation-plan", 1),
        ("src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs",
            "SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>", "Schedule") =>
            ("async-transfer", 2),
        ("src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs",
            "SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>", "NormalizeRoots") =>
            ("async-transfer", 2),
        ("src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs",
            "SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>", "CaptureTraversal") =>
            ("async-transfer", 3),
        ("src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs",
            "SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>", "BuildDataAccesses") =>
            ("async-transfer", 2),
        ("src/SomeEngine.ECS.Systems/JobEntity.cs",
            "SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor", "NormalizeAndValidate") =>
            ("owner-construction", 1),
        ("src/SomeEngine.ECS.Systems/JobEntityRuntime.cs",
            "SomeEngine.ECS.Systems.JobEntityRuntime", "CapturePackets") =>
            ("async-transfer", 2),
        ("src/SomeEngine.ECS.Systems/JobEntityRuntime.cs",
            "SomeEngine.ECS.Systems.JobEntityRuntime", "BuildPacketAccesses") =>
            ("async-transfer", 1),
        ("src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs",
            "SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>", "BuildCaptureAccesses") =>
            ("async-transfer", 1),
        ("src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs",
            "SomeEngine.ECS.Serialization.WorldWritePlan", "Build") =>
            ("serialization-boundary", 2),
        ("src/SomeEngine.ECS.Serialization/WorldSerializer.ManifestValidation.cs",
            "SomeEngine.ECS.Serialization.WorldSerializer", "SortManifest") =>
            ("serialization-boundary", 1),
        ("src/SomeEngine.ECS.SourceGen/BundleGenerator.cs",
            "SomeEngine.ECS.SourceGen.BundleGenerator", "GenerateSource") =>
            ("source-generation", 4),
        ("src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs",
            "SomeEngine.ECS.SourceGen.JobEntityGenerator", "BuildModel") =>
            ("source-generation", 1),
        ("src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs",
            "SomeEngine.ECS.SourceGen.JobEntityGenerator", "Generate") =>
            ("source-generation", 1),
        ("src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs",
            "SomeEngine.ECS.SourceGen.SerializationGenerator", "AddEnumSchema") =>
            ("source-generation", 1),
        _ => throw new InvalidOperationException(
            $"Unreviewed ToArray site: {site.File}:{site.Line} " +
            $"{node.FullName}.{site.ContainingMember}."),
    };

    return new ReviewedMaterializationSite(
        site.ContainingType,
        node.FullName,
        site.ContainingMember,
        site.File,
        site.Line,
        boundary,
        reviewedGroupCount);
}
#endif

sealed record ProjectSpec(
    string ProjectPath,
    Func<string, bool> Include,
    bool IncludeInCompiledScan);

sealed record LoadedProject(
    Project Project,
    Compilation Compilation,
    Func<string, bool> Include,
    bool IncludeInCompiledScan);

sealed record RetainedMember(
    string Name,
    string Kind,
    string Type,
    string Accessibility,
    bool ReadOnly);

sealed record CollectionBoundarySite(
    string ContainingType,
    string Member,
    string Role,
    string Type,
    string Accessibility);

sealed record ReviewedCollectionBoundarySite(
    string ContainingType,
    string Member,
    string Role,
    string Type,
    string Accessibility,
    string Boundary);

sealed record MaterializationSite(
    string ContainingType,
    string ContainingMember,
    string File,
    int Line);

sealed record ReviewedMaterializationSite(
    string ContainingTypeId,
    string ContainingType,
    string ContainingMember,
    string File,
    int Line,
    string Boundary,
    int ReviewedGroupCount);

sealed class MutableNode(
    string id,
    string assembly,
    string fullName,
    string name,
    string kind,
    string accessibility,
    RetainedMember[] retainedMembers,
    int declaredOrdinaryMethodCount,
    int declaredConstructorCount)
{
    public string Id { get; } = id;
    public string Assembly { get; } = assembly;
    public string FullName { get; } = fullName;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string Accessibility { get; } = accessibility;
    public RetainedMember[] RetainedMembers { get; } = retainedMembers;
    public int DeclaredDataMemberCount { get; } = retainedMembers.Length;
    public int DeclaredOrdinaryMethodCount { get; } = declaredOrdinaryMethodCount;
    public int DeclaredConstructorCount { get; } = declaredConstructorCount;
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    public int Rank { get; set; }
    public int Component { get; set; }
    public int Incoming { get; set; }
    public int Outgoing { get; set; }
}

sealed record CategorizedEdge(string Source, string Target, string Kind);

static class CompiledDependencyScanner
{
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value), static opcode => opcode);

    public static void AddAssemblyEdges(
        Stream image,
        IReadOnlyDictionary<string, string> nodeIdByMetadataId,
        HashSet<CategorizedEdge> edges)
    {
        using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
        MetadataReader reader = pe.GetMetadataReader();
        string assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        var provider = new TypeProvider(assembly);

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            string sourceMetadataId = DefinitionId(reader, handle, assembly);
            if (!nodeIdByMetadataId.ContainsKey(sourceMetadataId)) continue;

            TypeDefinition definition = reader.GetTypeDefinition(handle);
            AddEdges(
                sourceMetadataId,
                ResolveEntity(reader, definition.BaseType, assembly, provider),
                "inherits",
                nodeIdByMetadataId,
                edges);

            foreach (InterfaceImplementationHandle implementationHandle in definition.GetInterfaceImplementations())
            {
                InterfaceImplementation implementation = reader.GetInterfaceImplementation(implementationHandle);
                AddEdges(
                    sourceMetadataId,
                    ResolveEntity(reader, implementation.Interface, assembly, provider),
                    "implements",
                    nodeIdByMetadataId,
                    edges);
            }

            TypeDefinitionHandle declaringType = definition.GetDeclaringType();
            if (!declaringType.IsNil)
            {
                AddEdges(
                    DefinitionId(reader, declaringType, assembly),
                    [sourceMetadataId],
                    "contains",
                    nodeIdByMetadataId,
                    edges);
            }

            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                try
                {
                    AddEdges(
                        sourceMetadataId,
                        reader.GetFieldDefinition(fieldHandle).DecodeSignature(provider, null),
                        "signature",
                        nodeIdByMetadataId,
                        edges);
                }
                catch (BadImageFormatException)
                {
                }
            }

            foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                try
                {
                    MethodSignature<HashSet<string>> signature = method.DecodeSignature(provider, null);
                    AddEdges(sourceMetadataId, signature.ReturnType, "signature", nodeIdByMetadataId, edges);
                    foreach (HashSet<string> parameterType in signature.ParameterTypes)
                        AddEdges(sourceMetadataId, parameterType, "signature", nodeIdByMetadataId, edges);
                }
                catch (BadImageFormatException)
                {
                }

                AddGenericConstraints(
                    sourceMetadataId,
                    reader,
                    method.GetGenericParameters(),
                    assembly,
                    provider,
                    nodeIdByMetadataId,
                    edges);
                ScanBody(pe, reader, method, assembly, provider, sourceMetadataId, nodeIdByMetadataId, edges);
            }

            foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
            {
                try
                {
                    MethodSignature<HashSet<string>> signature = reader.GetPropertyDefinition(propertyHandle)
                        .DecodeSignature(provider, null);
                    AddEdges(sourceMetadataId, signature.ReturnType, "signature", nodeIdByMetadataId, edges);
                    foreach (HashSet<string> parameterType in signature.ParameterTypes)
                        AddEdges(sourceMetadataId, parameterType, "signature", nodeIdByMetadataId, edges);
                }
                catch (BadImageFormatException)
                {
                }
            }

            foreach (EventDefinitionHandle eventHandle in definition.GetEvents())
            {
                AddEdges(
                    sourceMetadataId,
                    ResolveEntity(reader, reader.GetEventDefinition(eventHandle).Type, assembly, provider),
                    "signature",
                    nodeIdByMetadataId,
                    edges);
            }

            AddGenericConstraints(
                sourceMetadataId,
                reader,
                definition.GetGenericParameters(),
                assembly,
                provider,
                nodeIdByMetadataId,
                edges);
        }
    }

    private static void AddGenericConstraints(
        string sourceMetadataId,
        MetadataReader reader,
        GenericParameterHandleCollection genericParameters,
        string assembly,
        TypeProvider provider,
        IReadOnlyDictionary<string, string> nodeIdByMetadataId,
        HashSet<CategorizedEdge> edges)
    {
        foreach (GenericParameterHandle genericParameterHandle in genericParameters)
        {
            GenericParameter genericParameter = reader.GetGenericParameter(genericParameterHandle);
            foreach (GenericParameterConstraintHandle constraintHandle in genericParameter.GetConstraints())
            {
                GenericParameterConstraint constraint = reader.GetGenericParameterConstraint(constraintHandle);
                AddEdges(
                    sourceMetadataId,
                    ResolveEntity(reader, constraint.Type, assembly, provider),
                    "signature",
                    nodeIdByMetadataId,
                    edges);
            }
        }
    }

    private static void ScanBody(
        PEReader pe,
        MetadataReader reader,
        MethodDefinition method,
        string assembly,
        TypeProvider provider,
        string sourceMetadataId,
        IReadOnlyDictionary<string, string> nodeIdByMetadataId,
        HashSet<CategorizedEdge> edges)
    {
        if (method.RelativeVirtualAddress == 0) return;

        MethodBodyBlock body;
        try
        {
            body = pe.GetMethodBody(method.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return;
        }

        byte[]? il = body.GetILBytes();
        if (il is null) return;
        int offset = 0;
        while (offset < il.Length)
        {
            ushort key = il[offset++];
            if (key == 0xfe && offset < il.Length) key = (ushort)(0xfe00 | il[offset++]);
            if (!Opcodes.TryGetValue(key, out OpCode opcode)) return;

            int operandSize = 0;
            bool hasTypeBearingToken = false;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    operandSize = 1;
                    break;
                case OperandType.InlineVar:
                    operandSize = 2;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    operandSize = 4;
                    hasTypeBearingToken = opcode.OperandType is OperandType.InlineField
                        or OperandType.InlineMethod
                        or OperandType.InlineTok
                        or OperandType.InlineType;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    operandSize = 8;
                    break;
                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length) return;
                    operandSize = checked(4 + BitConverter.ToInt32(il.AsSpan(offset, 4)) * 4);
                    break;
                default:
                    return;
            }

            if (offset + operandSize > il.Length) return;
            if (hasTypeBearingToken)
            {
                try
                {
                    EntityHandle entity = MetadataTokens.EntityHandle(BitConverter.ToInt32(il.AsSpan(offset, 4)));
                    string kind = opcode.Name is "newobj" or "newarr" ? "creates" : "uses";
                    AddEdges(
                        sourceMetadataId,
                        ResolveEntity(reader, entity, assembly, provider),
                        kind,
                        nodeIdByMetadataId,
                        edges);
                }
                catch (ArgumentException)
                {
                }
                catch (BadImageFormatException)
                {
                }
            }

            offset += operandSize;
        }
    }

    private static void AddEdges(
        string sourceMetadataId,
        IEnumerable<string> targetMetadataIds,
        string kind,
        IReadOnlyDictionary<string, string> nodeIdByMetadataId,
        HashSet<CategorizedEdge> edges)
    {
        if (!nodeIdByMetadataId.TryGetValue(sourceMetadataId, out string? source)) return;
        foreach (string targetMetadataId in targetMetadataIds)
        {
            if (targetMetadataId == sourceMetadataId ||
                !nodeIdByMetadataId.TryGetValue(targetMetadataId, out string? target))
                continue;
            edges.Add(new CategorizedEdge(source, target, kind));
        }
    }

    private static HashSet<string> ResolveEntity(
        MetadataReader reader,
        EntityHandle handle,
        string assembly,
        TypeProvider provider)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (handle.IsNil) return result;

        try
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    result.Add(DefinitionId(reader, (TypeDefinitionHandle)handle, assembly));
                    break;
                case HandleKind.TypeReference:
                    result.Add(ReferenceId(reader, (TypeReferenceHandle)handle, assembly));
                    break;
                case HandleKind.TypeSpecification:
                    result.UnionWith(reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                        .DecodeSignature(provider, null));
                    break;
                case HandleKind.MemberReference:
                    result.UnionWith(ResolveEntity(
                        reader,
                        reader.GetMemberReference((MemberReferenceHandle)handle).Parent,
                        assembly,
                        provider));
                    break;
                case HandleKind.MethodDefinition:
                    result.Add(DefinitionId(
                        reader,
                        reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType(),
                        assembly));
                    break;
                case HandleKind.FieldDefinition:
                    result.Add(DefinitionId(
                        reader,
                        reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType(),
                        assembly));
                    break;
                case HandleKind.MethodSpecification:
                    MethodSpecification specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    result.UnionWith(ResolveEntity(reader, specification.Method, assembly, provider));
                    foreach (HashSet<string> argument in specification.DecodeSignature(provider, null))
                        result.UnionWith(argument);
                    break;
            }
        }
        catch (BadImageFormatException)
        {
        }

        return result;
    }

    private static string DefinitionId(MetadataReader reader, TypeDefinitionHandle handle, string assembly) =>
        assembly + "::" + DefinitionFullName(reader, handle);

    private static string DefinitionFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil) return DefinitionFullName(reader, declaringType) + "+" + name;
        string namespaceName = reader.GetString(definition.Namespace);
        return namespaceName.Length == 0 ? name : namespaceName + "." + name;
    }

    private static string ReferenceId(MetadataReader reader, TypeReferenceHandle handle, string currentAssembly)
    {
        (string assembly, string fullName) = ReferenceParts(reader, handle, currentAssembly);
        return assembly + "::" + fullName;
    }

    private static (string Assembly, string FullName) ReferenceParts(
        MetadataReader reader,
        TypeReferenceHandle handle,
        string currentAssembly)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        string namespaceName = reader.GetString(reference.Namespace);
        EntityHandle scope = reference.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
        {
            (string assembly, string fullName) = ReferenceParts(
                reader,
                (TypeReferenceHandle)scope,
                currentAssembly);
            return (assembly, fullName + "+" + name);
        }

        string assemblyName = currentAssembly;
        if (scope.Kind == HandleKind.AssemblyReference)
            assemblyName = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        string typeName = namespaceName.Length == 0 ? name : namespaceName + "." + name;
        return (assemblyName, typeName);
    }

    private sealed class TypeProvider(string assembly)
        : ISignatureTypeProvider<HashSet<string>, object?>
    {
        public HashSet<string> GetArrayType(HashSet<string> elementType, ArrayShape shape) => Copy(elementType);
        public HashSet<string> GetByReferenceType(HashSet<string> elementType) => Copy(elementType);

        public HashSet<string> GetFunctionPointerType(MethodSignature<HashSet<string>> signature) =>
            Merge([signature.ReturnType, .. signature.ParameterTypes]);

        public HashSet<string> GetGenericInstantiation(
            HashSet<string> genericType,
            ImmutableArray<HashSet<string>> typeArguments) =>
            Merge([genericType, .. typeArguments]);

        public HashSet<string> GetGenericMethodParameter(object? genericContext, int index) => Empty();
        public HashSet<string> GetGenericTypeParameter(object? genericContext, int index) => Empty();

        public HashSet<string> GetModifiedType(
            HashSet<string> modifier,
            HashSet<string> unmodifiedType,
            bool isRequired) => Merge([modifier, unmodifiedType]);

        public HashSet<string> GetPinnedType(HashSet<string> elementType) => Copy(elementType);
        public HashSet<string> GetPointerType(HashSet<string> elementType) => Copy(elementType);
        public HashSet<string> GetPrimitiveType(PrimitiveTypeCode typeCode) => Empty();
        public HashSet<string> GetSZArrayType(HashSet<string> elementType) => Copy(elementType);

        public HashSet<string> GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => One(DefinitionId(reader, handle, assembly));

        public HashSet<string> GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => One(ReferenceId(reader, handle, assembly));

        public HashSet<string> GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static HashSet<string> Empty() => new(StringComparer.Ordinal);

        private static HashSet<string> One(string value) => new(StringComparer.Ordinal) { value };

        private static HashSet<string> Copy(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

        private static HashSet<string> Merge(IEnumerable<HashSet<string>> sets)
        {
            var result = Empty();
            foreach (HashSet<string> set in sets) result.UnionWith(set);
            return result;
        }
    }
}

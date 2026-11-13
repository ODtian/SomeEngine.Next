using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SomeEngine.Render.Tests.Architecture;

public sealed class RenderArchitectureChecks
{
    [Fact]
    public void UploadPagesHaveOneRepresentation()
    {
        Type[] pages = typeof(RenderGraph.RenderGraph).Assembly.GetTypes()
            .Where(type => type.Name == "UploadPage").ToArray();
        Assert.Single(pages);
    }

    [Fact]
    public void FrameDictionaryKeysDescribeResourcesWithoutLabels()
    {
        Type frame = typeof(RenderGraph.RenderGraph).Assembly.GetType("SomeEngine.RenderGraph.RenderGraphFrameState", throwOnError: true)!;
        foreach (FieldInfo field in frame.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Type type = field.FieldType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                Assert.False(ContainsString(type.GetGenericArguments()[0]), $"ARCH-20: {field.Name}");
        }
    }

    [Fact]
    public void ClusterPageBakingBelongsToImporters()
    {
        Assert.DoesNotContain(typeof(SomeEngine.Assets.Schema.ClusterGeometryBlock).Assembly.GetTypes(),
            type => type.Name == "ClusterMeshData");
        Type baker = typeof(SomeEngine.Assets.Importers.ClusterBuilder).Assembly.GetType("SomeEngine.Assets.Importers.ClusterMeshData", throwOnError: true)!;
        Assert.False(baker.IsPublic, "ARCH-13: ClusterMeshData belongs to the importer's implementation.");
        Assert.DoesNotContain(typeof(SomeEngine.Assets.Schema.ClusterGeometryBlock).GetMethods(),
            method => method.Name is "CreateAsync" or "OpenPayloadSourceAsync" or "LoadPagesAsync");
    }

    private static readonly string[] RenderingAssemblies =
    [
        "SomeEngine.Render", "SomeEngine.Render.Cluster", "SomeEngine.Render.Cluster.Pipeline",
        "SomeEngine.Render.Features", "SomeEngine.Render.MaterialBindless", "SomeEngine.Terrain",
        "SomeEngine.Elements.Render", "SomeEngine.Runtime",
    ];
    private static readonly string[] GraphicsAssemblies =
    [
        "SomeEngine.Graphics", "SomeEngine.Graphics.Direct3D12", "SomeEngine.Graphics.Vulkan",
        "SomeEngine.Graphics.Validation", "SomeEngine.Graphics.Direct3D12.Validation",
    ];
    private static readonly HashSet<string> ForbiddenWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authoring", "Lease", "Cursor", "Plan", "Slot", "Owner", "Runtime", "Publication", "Admission",
        "Canonical", "Exact", "Stable", "Physical", "Logical", "Prepared", "Pending", "Snapshot", "Fingerprint",
        "Module", "Program", "Sink", "Contract", "Abi", "Invocation",
    };
    private const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void EngineDeclarationsUseConcreteDataAndBehaviorNames()
    {
        var errors = new List<string>();
        foreach (Assembly assembly in RenderingAssemblies.Concat(GraphicsAssemblies).Append("SomeEngine.RenderGraph").Select(Assembly.Load))
        foreach (Type type in assembly.GetTypes().Where(type => !Generated(type)))
        {
            Check(type.Name, type.FullName!);
            if (type.IsGenericTypeDefinition)
                foreach (Type argument in type.GetGenericArguments()) Check(argument.Name, $"{type.FullName}<{argument.Name}>");
            foreach (MemberInfo member in type.GetMembers(DeclaredMembers))
            {
                if (member is Type || Generated(member) || member.Name.StartsWith('<')) continue;
                if (member is PropertyInfo { Name: "EqualityContract" } property && Generated(property.GetMethod!)) continue;
                if (member is MethodInfo method && ImplementsExternalMember(type, method)) continue;
                if (member is MethodBase methodBase)
                {
                    if (member is not ConstructorInfo && !methodBase.IsSpecialName) Check(member.Name, $"{type.FullName}.{member.Name}");
                    if (methodBase is MethodInfo { IsGenericMethodDefinition: true } generic)
                        foreach (Type argument in generic.GetGenericArguments()) Check(argument.Name, $"{type.FullName}.{member.Name}<{argument.Name}>");
                    foreach (ParameterInfo parameter in methodBase.GetParameters())
                        Check(parameter.Name ?? "", $"{type.FullName}.{member.Name}({parameter.Name})");
                }
                else if (member is FieldInfo or PropertyInfo) Check(member.Name, $"{type.FullName}.{member.Name}");
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        void Check(string name, string member)
        {
            name = name[(name.LastIndexOf('.') + 1)..];
            foreach (Match word in Regex.Matches(name, "[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[0-9]+"))
                if (ForbiddenWords.Contains(word.Value)) errors.Add($"ARCH-01: {member} contains {word.Value}.");
        }
    }

    [Fact]
    public void RenderingUsesGraphResourceStateAndFrameCompletion()
    {
        var errors = new List<string>();
        foreach (Type type in RenderingAssemblies.Select(Assembly.Load).SelectMany(assembly => assembly.GetTypes()))
        {
            foreach (MemberInfo member in type.GetMembers(DeclaredMembers))
            {
                Type? value = member switch { FieldInfo field => field.FieldType, PropertyInfo property => property.PropertyType, _ => null };
                if (value is not null)
                {
                    if (ContainsType(value, static nested => nested.Name is "BufferBoundaryState" or "TextureBoundaryState"))
                        errors.Add($"ARCH-02: {type.FullName}.{member.Name}");
                    if (ContainsType(value, static nested => nested.Name == "QueueCompletion"))
                        errors.Add($"ARCH-03: {type.FullName}.{member.Name}");
                }
                if (member is MethodBase method)
                {
                    foreach (ParameterInfo parameter in method.GetParameters())
                        CheckBoundary(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})");
                    if (method is MethodInfo info) CheckBoundary(info.ReturnType, $"{type.FullName}.{method.Name} return");
                }
                if (member is FieldInfo or PropertyInfo &&
                    new[] { "_pending", "_committed", "_staged" }.Any(prefix => member.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"ARCH-14: {type.FullName}.{member.Name}");
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        void CheckBoundary(Type type, string member)
        {
            if (ContainsType(type, static nested => nested.Name is "BufferBoundaryState" or "TextureBoundaryState"))
                errors.Add($"ARCH-02: {member}");
        }
    }

    [Fact]
    public void FrameObserversReceiveOnlyTheCompleteSubmission()
    {
        Type observer = typeof(RenderGraph.IRenderGraphFrameExecutionObserver);
        MethodInfo method = Assert.Single(observer.GetMethods());
        Assert.Equal("OnFrameSubmitted", method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        ParameterInfo parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(RenderGraph.RenderGraphSubmission).MakeByRefType(), parameter.ParameterType);
        Assert.True(parameter.IsIn, "ARCH-05: Submission is passed by readonly reference.");
        Type submission = typeof(RenderGraph.RenderGraphSubmission);
        Assert.True(submission.IsByRefLike);
        Assert.Equal("FrameIdentity", Assert.Single(submission.GetProperties(DeclaredMembers)).Name);
        Assert.Equal("TryGetReadbackCompletion", Assert.Single(submission.GetMethods(DeclaredMembers), candidate => candidate.IsPublic && !candidate.IsSpecialName).Name);
    }

    [Fact]
    public void FeatureAssembliesDependOnNeutralRenderingData()
    {
        Assembly features = Assembly.Load("SomeEngine.Render.Features");
        Assert.DoesNotContain(features.GetReferencedAssemblies(), reference => reference.Name is
            "SomeEngine.Render.Cluster" or "SomeEngine.Render.Cluster.Pipeline");
        string root = RepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj"));
        Assert.DoesNotContain("tools/", project.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistentSchedulingHasOneGraphHolder()
    {
        Assembly graph = typeof(RenderGraph.RenderGraph).Assembly;
        Type schedule = graph.GetType("SomeEngine.RenderGraph.GraphSchedule", true)!;
        Assert.Single(typeof(RenderGraph.RenderGraph).GetFields(DeclaredMembers), field => field.FieldType == schedule);
        Type frame = graph.GetType("SomeEngine.RenderGraph.FrameResources", true)!;
        Assert.DoesNotContain(frame.GetFields(DeclaredMembers), field => ContainsType(field.FieldType,
            static type => type.Name is "GraphSchedule" or "GraphEditChanges"));
        foreach (Type recording in new[] { frame, graph.GetType("SomeEngine.RenderGraph.FrameRecording", true)! })
            Assert.DoesNotContain(recording.GetFields(DeclaredMembers), field => field.FieldType == typeof(bool) &&
                new[] { "Changed", "Ready", "Reuse", "Initialized" }.Any(suffix => field.Name.EndsWith(suffix, StringComparison.Ordinal)));
    }

    [Fact]
    public void ClusterPipelineTypesDescribeGeometryShadingAndGraphInstallation()
    {
        Assembly pipeline = Assembly.Load("SomeEngine.Render.Cluster.Pipeline");
        Assert.DoesNotContain(pipeline.GetTypes(), type => new[] { "Shadow", "Vsm", "Transparency", "Decal", "Blend" }
            .Any(word => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Type geometry = pipeline.GetType("SomeEngine.Render.Cluster.Pipeline.ClusterGeometryPipeline", true)!;
        Assert.DoesNotContain(geometry.GetMethods(), method => method.Name == "StageViews");
        foreach (string name in new[] { "ClusterMaterialAssignments", "ClusterRasterStages", "ClusterShadeStages" })
            Assert.DoesNotContain(pipeline.GetType("SomeEngine.Render.Cluster.Pipeline." + name, true)!.GetMethods(DeclaredMembers),
                method => method.Name == "AcceptChanges");
        Type meshes = Assembly.Load("SomeEngine.Render.Cluster").GetType("SomeEngine.Render.Cluster.ClusterMeshes", true)!;
        Assert.DoesNotContain(meshes.GetFields(DeclaredMembers), field => field.FieldType == typeof(SemaphoreSlim));
    }

    [Fact]
    public void RecordedCommandsExposeTheirSubmissionStatus()
    {
        Type commands = typeof(SomeEngine.Graphics.RecordedCommands);
        Assert.True(commands.IsSealed);
        Assert.DoesNotContain(commands.GetFields(DeclaredMembers), field => field.FieldType == typeof(object));
        Assert.Equal(["Executable", "Submitted", "Discarded"], Enum.GetNames<SomeEngine.Graphics.RecordedCommandsStatus>());
        Assert.DoesNotContain(commands.Assembly.GetTypes(), type => type.Name == "RecordedCommandsLease");
    }

    [Fact]
    public void WorldComponentsCarryFeedbackValues()
    {
        var errors = new List<string>();
        foreach (Type type in RenderingAssemblies.Append("SomeEngine.Animation.Pose").Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes()).Where(type => type.GetInterfaces()
                .Any(marker => marker.FullName is "SomeEngine.ECS.IComponent" or "SomeEngine.ECS.Components.IBufferElement")))
            foreach (FieldInfo field in type.GetFields(DeclaredMembers))
                if (ContainsType(field.FieldType, static nested => typeof(Delegate).IsAssignableFrom(nested)
                    || typeof(Task).IsAssignableFrom(nested) || nested.Name.StartsWith("TaskCompletionSource`", StringComparison.Ordinal)))
                    errors.Add($"ARCH-22: {type.FullName}.{field.Name}");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static bool Generated(MemberInfo member) => member.IsDefined(typeof(CompilerGeneratedAttribute), false)
        || member.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false)
        || member.DeclaringType?.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false) == true
        || member.Name.StartsWith('<');
    private static bool ImplementsExternalMember(Type type, MethodInfo method)
    {
        if (type.IsInterface) return false;
        foreach (Type external in type.GetInterfaces().Where(contract => !(contract.Namespace ?? "").StartsWith("SomeEngine.")))
            if (type.GetInterfaceMap(external).TargetMethods.Contains(method)) return true;
        return false;
    }
    private static bool ContainsType(Type type, Func<Type, bool> match) => match(type)
        || type.HasElementType && ContainsType(type.GetElementType()!, match)
        || type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsType(argument, match));
    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")) || File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool ContainsString(Type type) => type == typeof(string)
        || type.IsGenericType && type.GetGenericArguments().Any(ContainsString);
}

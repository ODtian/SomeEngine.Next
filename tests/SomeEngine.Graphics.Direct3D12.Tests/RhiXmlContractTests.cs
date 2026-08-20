using System.Reflection;
using System.Xml.Linq;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class RhiXmlContractTests
{
    [Fact]
    public void Every_public_RHI_type_declares_one_concurrency_default_and_links_LIFE_007()
    {
        Assembly[] assemblies =
        [
            typeof(Device).Assembly,
            typeof(D3D12Backend).Assembly,
            typeof(ValidationLayer).Assembly,
        ];

        foreach (Assembly assembly in assemblies)
        {
            IReadOnlyDictionary<string, XElement> documentation = LoadDocumentation(assembly);
            foreach (Type type in assembly.GetExportedTypes())
            {
                string typeName = type.FullName
                    ?? throw new InvalidOperationException($"The exported type {type} has no full name.");
                string memberName = $"T:{typeName.Replace('+', '.')}";
                Assert.True(
                    documentation.TryGetValue(memberName, out XElement? member),
                    $"{memberName} has no generated XML documentation.");

                string remarks = string.Join(
                    " ",
                    member.Elements("remarks").Select(static element => element.Value));
                Assert.Contains("Thread safety:", remarks, StringComparison.Ordinal);
                Assert.Contains("Ownership:", remarks, StringComparison.Ordinal);
                Assert.Contains("After Dispose:", remarks, StringComparison.Ordinal);
                bool hasDisposablePattern =
                    typeof(IDisposable).IsAssignableFrom(type) ||
                    type.GetMethod(
                        "Dispose",
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: Type.EmptyTypes,
                        modifiers: null) is not null;
                bool lifetimeSensitive =
                    type == typeof(IGraphicsBackend) ||
                    typeof(GraphicsObject).IsAssignableFrom(type) ||
                    typeof(DeviceCapability).IsAssignableFrom(type) ||
                    hasDisposablePattern;
                if (lifetimeSensitive)
                {
                    bool threadSafe = remarks.Contains("Thread-safe", StringComparison.Ordinal);
                    bool externallySynchronized = remarks.Contains(
                        "Externally synchronized",
                        StringComparison.Ordinal);
                    Assert.True(
                        threadSafe ^ externallySynchronized,
                        $"{memberName} must declare one unambiguous concurrency default.");
                    Assert.Contains("RHI-LIFE-001", remarks, StringComparison.Ordinal);
                    Assert.Contains("RHI-LIFE-002", remarks, StringComparison.Ordinal);
                    Assert.Contains("RHI-LIFE-007", remarks, StringComparison.Ordinal);
                    string[] requiredAnchors =
                        ["rhi-life-001", "rhi-life-002", "rhi-life-007"];
                    foreach (string anchor in requiredAnchors)
                    {
                        Assert.Contains(
                            member.Descendants("see"),
                            element =>
                                ((string?)element.Attribute("href"))?.Contains(
                                    $"Lifetime-Concurrency-and-Diagnostics.md#{anchor}",
                                    StringComparison.Ordinal) == true);
                    }
                }
                if (hasDisposablePattern)
                {
                    Assert.Contains(
                        "Concurrent Dispose calls are safe",
                        remarks,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain("no Dispose operation", remarks, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Work_graph_dispatch_grid_limits_are_public_immutable_and_documented()
    {
        IReadOnlyDictionary<string, XElement> documentation =
            LoadDocumentation(typeof(WorkGraphs).Assembly);
        string[] propertyNames =
        [
            nameof(WorkGraphs.MaximumDispatchGridDimension),
            nameof(WorkGraphs.MaximumDispatchGridVolume),
            nameof(WorkGraphs.MaximumOneDimensionalDispatchGridX),
        ];

        foreach (string propertyName in propertyNames)
        {
            PropertyInfo property = typeof(WorkGraphs).GetProperty(propertyName)
                ?? throw new InvalidOperationException($"Missing WorkGraphs.{propertyName}.");
            Assert.Equal(typeof(uint), property.PropertyType);
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod!.IsPublic);
            Assert.Null(property.SetMethod);

            string memberName = $"P:{typeof(WorkGraphs).FullName}.{propertyName}";
            Assert.True(
                documentation.TryGetValue(memberName, out XElement? member),
                $"{memberName} has no generated XML documentation.");
            Assert.False(string.IsNullOrWhiteSpace(member.Element("summary")?.Value));
        }
    }

    [Fact]
    public void NodeIndex_is_confined_to_linked_adapter_affinity_values()
    {
        Type[] permittedOwners =
        [
            typeof(DescriptorTable),
            typeof(DeviceQueueDesc),
            typeof(QueryPoolDesc),
            typeof(Queue),
        ];

        PropertyInfo[] properties =
        [
            .. typeof(Device).Assembly
                .GetExportedTypes()
                .SelectMany(static type => type.GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly))
                .Where(static property => property.Name == "NodeIndex"),
        ];

        Assert.Equal(
            permittedOwners.OrderBy(static type => type.FullName),
            properties
                .Select(static property => property.DeclaringType!)
                .OrderBy(static type => type.FullName));
        Assert.All(properties, static property => Assert.Equal(typeof(uint), property.PropertyType));
        Assert.Null(typeof(CommandContextDesc).GetProperty("NodeIndex"));
        Assert.Null(typeof(CommandContext).GetProperty("NodeIndex"));
        Assert.Null(typeof(WorkGraphEntryPointInfo).GetProperty("NodeIndex"));
        Assert.Null(typeof(WorkGraphDispatchDesc).GetProperty("EntryPointIndex"));
    }

    private static IReadOnlyDictionary<string, XElement> LoadDocumentation(Assembly assembly)
    {
        string assemblyPath = assembly.Location;
        string xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
        Assert.True(File.Exists(xmlPath), $"Generated XML documentation is missing: {xmlPath}");

        XDocument document = XDocument.Load(xmlPath, LoadOptions.None);
        return document
            .Descendants("member")
            .ToDictionary(
                static member => (string)member.Attribute("name")!,
                static member => member,
                StringComparer.Ordinal);
    }
}

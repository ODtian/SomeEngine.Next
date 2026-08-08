using System.Reflection;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class PublicApiShapeTests
{
    [Fact]
    public void SystemsAssembly_DoesNotExportRemovedGlobalSchedulingModel()
    {
        string[] removedTypeNames =
        [
            "SystemAccessManifest",
            "AccessConflicts",
            "SystemSchedule",
            "GlobalDependency",
        ];

        Type[] exportedTypes = typeof(ISystemDriver<>).Assembly.GetExportedTypes();
        foreach (string removedTypeName in removedTypeNames)
        {
            Assert.DoesNotContain(
                exportedTypes,
                type => WithoutGenericArity(type.Name) == removedTypeName);
        }
    }

    [Fact]
    public void ConcreteDescriptorsOwnMultipleSemanticFacts()
    {
        Type[] descriptors = typeof(ISystemDriver<>).Assembly.GetTypes()
            .Where(static type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                !type.IsEnum &&
                IsDescriptorName(type.Name))
            .ToArray();

        Assert.NotEmpty(descriptors);
        Assert.All(
            descriptors,
            static descriptor =>
            {
                FieldInfo[] fields = descriptor.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Where(static field => !field.IsStatic)
                    .ToArray();
                Assert.True(
                    fields.Length > 1,
                    $"{descriptor.FullName} is a single-field descriptor wrapper. " +
                    "A descriptor must own independent normalized facts and invariants.");
            });
    }

    private static bool IsDescriptorName(string name)
    {
        string simpleName = WithoutGenericArity(name);
        return simpleName.EndsWith("Descriptor", StringComparison.Ordinal) ||
               simpleName.EndsWith("Desc", StringComparison.Ordinal);
    }

    private static string WithoutGenericArity(string name)
    {
        int marker = name.IndexOf('`');
        return marker < 0 ? name : name[..marker];
    }
}

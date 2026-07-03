using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ApiContractTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void DeclaredApiContractsExist()
    {
        var failures = new List<string>();

        foreach (var shape in Config.ApiContracts)
        {
            var assemblyPath = FindAssembly(shape.Assembly);
            if (assemblyPath is null)
            {
                failures.Add($"Assembly {shape.Assembly} must build before API contract {shape.Type} can be consumed");
                continue;
            }

            var type = FindType(assemblyPath, shape.Type);
            if (type is null)
            {
                failures.Add($"Type {shape.Type} must exist in assembly {shape.Assembly}");
                continue;
            }
            else if (!type.Value.IsPublic)
            {
                failures.Add($"Type {shape.Type} must be public");
                continue;
            }

            foreach (var member in shape.Members)
            {
                if (!type.Value.TryGetMember(member, out bool memberIsPublic))
                {
                    failures.Add($"Type {shape.Type} must expose {member.Kind} member {member.Name}");
                }
                else if (!memberIsPublic)
                {
                    failures.Add($"Type {shape.Type} member {member.Name} must be public");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared API contracts are not available:\n" + string.Join("\n", failures));
    }

    private static string? FindAssembly(string name)
    {
        var declaredProject = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .FirstOrDefault(project => project.Name == name);

        if (declaredProject is null)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(Path.Combine(HarnessConfig.ResolveRepoRoot(), declaredProject.Path));
        if (projectDirectory is null || !Directory.Exists(projectDirectory))
        {
            return null;
        }

        return Directory.GetFiles(projectDirectory, $"{name}.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static ApiTypeFact? FindType(string assemblyPath, string fullName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            string name = metadata.GetString(definition.Name);
            string ns = metadata.GetString(definition.Namespace);
            string candidate = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (candidate == fullName)
            {
                return new ApiTypeFact(IsPublic(definition), ReadMembers(metadata, definition));
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, bool> ReadMembers(MetadataReader metadata, TypeDefinition definition)
    {
        var members = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var handle in definition.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(handle);
            string name = metadata.GetString(property.Name);
            var accessors = property.GetAccessors();
            bool isPublic = IsPublic(metadata, accessors.Getter)
                || IsPublic(metadata, accessors.Setter)
                || accessors.Others.Any(handle => IsPublic(metadata, handle));

            AddOrUpgrade(members, $"Property:{name}", isPublic);
        }

        foreach (var handle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.SpecialName) == MethodAttributes.SpecialName)
            {
                continue;
            }

            string name = metadata.GetString(method.Name);
            AddOrUpgrade(members, $"Method:{name}", IsPublic(method.Attributes));
        }

        foreach (var handle in definition.GetFields())
        {
            var field = metadata.GetFieldDefinition(handle);
            string name = metadata.GetString(field.Name);
            AddOrUpgrade(members, $"Field:{name}", IsPublic(field.Attributes));
        }

        return members;
    }

    private static void AddOrUpgrade(Dictionary<string, bool> members, string key, bool isPublic)
    {
        if (!members.TryGetValue(key, out bool existing) || (!existing && isPublic))
        {
            members[key] = isPublic;
        }
    }

    private static bool IsPublic(TypeDefinition definition)
    {
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        return visibility is TypeAttributes.Public or TypeAttributes.NestedPublic;
    }

    private static bool IsPublic(MethodAttributes attributes)
    {
        var visibility = attributes & MethodAttributes.MemberAccessMask;
        return visibility is MethodAttributes.Public;
    }

    private static bool IsPublic(MetadataReader metadata, MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
        {
            return false;
        }

        return IsPublic(metadata.GetMethodDefinition(handle).Attributes);
    }

    private static bool IsPublic(FieldAttributes attributes)
    {
        var visibility = attributes & FieldAttributes.FieldAccessMask;
        return visibility is FieldAttributes.Public;
    }

    private readonly record struct ApiTypeFact(bool IsPublic, IReadOnlyDictionary<string, bool> Members)
    {
        public bool TryGetMember(ApiMemberContractConfig member, out bool isPublic)
        {
            string key = $"{member.Kind}:{member.Name}";
            return Members.TryGetValue(key, out isPublic);
        }
    }
}

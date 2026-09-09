using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeShaderBoundaryTests
{
    private static readonly HashSet<string> CompilerReflectionTypes =
    [
        "AttributeReflection",
        "EntryPointReflection",
        "FunctionReflection",
        "ICompileRequest",
        "IComponentType",
        "IEntryPoint",
        "IGlobalSession",
        "IMetadata",
        "IModule",
        "ISession",
        "ISlangBlob",
        "ShaderReflection",
        "TypeLayoutReflection",
        "TypeReflection",
        "VariableLayoutReflection",
        "VariableReflection",
    ];

    private static readonly HashSet<string> CompilerOperations =
    [
        "FindEntryPointByName",
        "GetEntryPointCode",
        "GetLayout",
        "Link",
        "LoadModuleFromSource",
    ];

    [Fact]
    public void ShippingRuntimeDoesNotDeployShaderCompilerBinaries()
    {
        string[] compilerFiles =
        [
            "dxcompiler.dll",
            "dxil.dll",
            "slang-compiler.dll",
            "slang-glslang.dll",
            "libslang-compiler.so",
            "libslang-compiler.dylib",
        ];

        Assert.All(compilerFiles, file =>
            Assert.False(
                File.Exists(Path.Combine(AppContext.BaseDirectory, file)),
                $"Shipping runtime output contains shader compiler binary '{file}'."));
    }

    [Fact]
    public void ShippingAssembliesUseOnlyDetachedSlangProgramTypes()
    {
        string[] assemblies = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "SomeEngine.*.dll")
            .Where(static path =>
                !Path.GetFileName(path).Contains(".Tests", StringComparison.Ordinal) &&
                !Path.GetFileName(path).Contains("SourceGen", StringComparison.Ordinal) &&
                !Path.GetFileName(path).Contains("Generators", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(assemblies);
        foreach (string assembly in assemblies)
            AssertDetachedBoundary(assembly);
    }

    private static void AssertDetachedBoundary(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();

        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            Assert.NotEqual("SomeEngine.Assets.Importers", name);
        }

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference type = metadata.GetTypeReference(handle);
            if (!string.Equals(
                    metadata.GetString(type.Namespace),
                    "SlangShaderSharp",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string name = metadata.GetString(type.Name);
            Assert.DoesNotContain(name, CompilerReflectionTypes);
        }

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            TypeReference owner = metadata.GetTypeReference(
                (TypeReferenceHandle)member.Parent);
            if (!string.Equals(
                    metadata.GetString(owner.Namespace),
                    "SlangShaderSharp",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string name = metadata.GetString(member.Name);
            Assert.DoesNotContain(name, CompilerOperations);
        }
    }
}

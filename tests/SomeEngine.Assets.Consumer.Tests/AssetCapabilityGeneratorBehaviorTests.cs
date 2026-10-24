using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Generators;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetCapabilityGeneratorBehaviorTests
{
    private const string ExtensionSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using SomeEngine.Assets;
        using SomeEngine.Assets.Importing;
        using SomeEngine.Serialization;
        namespace Extension;
        [AssetBlock("example.extension-data"), BinarySerializable, BinaryFields(nameof(Value))]
        public partial class Data : AssetBlock { public partial int Value { get; set; } }
        public sealed class Source;
        public sealed record Settings(int Value);
        public sealed class Dependency { public int Number => 47; }
        [AssetImporter("bcc64662-e334-49b1-b559-55d89b06e351", typeof(Data))]
        public class Importer : IAssetImporter<Source, Settings>
        {
            public Importer(Dependency dependency) { Number = dependency.Number; }
            public int Number { get; }
            public ValueTask ImportAsync(Source source, Settings settings, ImportContext context,
                ImportOutput output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
        """;

    [Fact]
    public void ImporterCompilesWithoutAnySourceGenerator()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using SomeEngine.Assets;
            using SomeEngine.Assets.Importing;
            using SomeEngine.Assets.Schema;
            [AssetImporter("e2287123-a55b-4d97-92f8-c51c03baedce", typeof(MeshBlock))]
            public sealed class Importer : IAssetImporter<object, int>
            {
                public ValueTask ImportAsync(object source, int settings, ImportContext context,
                    ImportOutput output, CancellationToken token) => ValueTask.CompletedTask;
            }
            public static class Consumer
            {
                public static bool Execute() => new Importer() is IAssetImporter<object, int>;
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source, generators: false)))!);
    }

    [Fact]
    public void ReferencedCompleteImporterIsRegisteredWithoutRegeneratingItsType()
    {
        byte[] extension = Emit(Compile(ExtensionSource, capabilities: false));
        const string source = """
            using System;
            using System.Linq;
            using Microsoft.Extensions.DependencyInjection;
            using SomeEngine.Assets;
            using SomeEngine.Assets.Importing;
            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static bool Execute()
                {
                    var services = new ServiceCollection();
                    services.AddSingleton(new Extension.Dependency());
                    ProjectAssets.AddAssetCapabilities(services);
                    using var provider = services.BuildServiceProvider();
                    using var scope = provider.CreateScope();
                    var importer = scope.ServiceProvider.GetRequiredService<Extension.Importer>();
                    var typed = scope.ServiceProvider.GetServices<IAssetImporter<Extension.Source, Extension.Settings>>().Single();
                    var metadata = provider.GetServices<AssetImporterCapability>().Single(value => value.ImporterType == typeof(Extension.Importer));
                    return importer.Number == 47 && object.ReferenceEquals(importer, typed)
                        && metadata.Id == new Guid("bcc64662-e334-49b1-b559-55d89b06e351")
                        && metadata.SourceType == typeof(Extension.Source) && metadata.SettingsType == typeof(Extension.Settings)
                        && metadata.ProducedBlocks.Single().Id == Extension.Data.BlockTypeId
                        && ProjectAssets.BlockCapabilities.Any(value => value.BlockType == typeof(Extension.Data));
                }
            }
            """;
        byte[] consumer = Emit(Compile(source, dependencies: [MetadataReference.CreateFromImage(extension)]));
        Assert.True((bool)Execute(consumer, extension)!);
    }

    [Fact]
    public void GeneratedChoicesUseLibraryBlocksWithoutProjectInitialization()
    {
        byte[] extension = Emit(Compile(ExtensionSource, capabilities: false));
        const string source = """
            using System;
            using System.Linq;
            using SomeEngine.Assets;
            using SomeEngine.Assets.Schema;
            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static bool Execute()
                {
                    using var asset = new MeshAsset();
                    using var content = new Extension.Data { Value = 61 };
                    asset.SetBlock(content);
                    var blockChoice = ProjectAssets.BlockChoices.Single(choice => choice.Value == ProjectAssets.BlockKind.Data);
                    var importerChoice = ProjectAssets.ImporterChoices.Single(choice => choice.Value == ProjectAssets.ImporterKind.Importer);
                    var selection = ProjectAssets.SelectImporter(importerChoice.Value);
                    var selected = ProjectAssets.GetBlockAsync(asset, blockChoice.Value).GetAwaiter().GetResult();
                    return blockChoice.Id == Extension.Data.BlockTypeId && blockChoice.Label == "Data"
                        && selection.ImporterId == importerChoice.Id
                        && selection.ImporterId == new Guid("bcc64662-e334-49b1-b559-55d89b06e351")
                        && ProjectAssets.ContainsBlock(asset, blockChoice.Value)
                        && ReferenceEquals(selected, asset.GetBlock<Extension.Data>())
                        && ((Extension.Data)selected).Value == 61;
                }
            }
            """;
        byte[] consumer = Emit(Compile(source, dependencies: [MetadataReference.CreateFromImage(extension)]));
        Assert.True((bool)Execute(consumer, extension)!);
    }

    [Fact]
    public void DifferentProjectsCanUseTheirOwnBlockSetsInTheSameProcess()
    {
        foreach (string name in new[] { "First", "Second" })
        {
            string source = PlainBlock(name, "Data", "example.independent-" + name) + """

                [SomeEngine.Assets.AssetCapabilities] public static partial class ProjectAssets;
                public static class Consumer
                {
                    public static bool Execute()
                    {
                        using var asset = new SomeEngine.Assets.Schema.MeshAsset();
                        using var block = new Data(83);
                        asset.SetBlock(block);
                        return asset.GetBlockAsync<Data>().GetAwaiter().GetResult().Value == 83;
                    }
                }
                """;
            Assert.True((bool)Execute(Emit(Compile(source)))!);
        }
    }

    [Fact]
    public void BusinessChangesChangeVersionButAnAdditionalRegistrationDoesNot()
    {
        const string read = """

            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static string Execute() => System.Linq.Enumerable.Single(
                    ProjectAssets.ImporterCapabilities,
                    value => value.ImporterType == typeof(Extension.Importer)).ImplementationVersion;
            }
            """;
        string original = (string)Execute(Emit(Compile(ExtensionSource + read)))!;
        string changed = (string)Execute(Emit(Compile(ExtensionSource.Replace("=> 47", "=> 48", StringComparison.Ordinal) + read)))!;
        string extra = (string)Execute(Emit(Compile(ExtensionSource + read + "\n[AssetCapabilities] public static partial class ExtraRegistration;")))!;
        Assert.NotEmpty(original);
        Assert.NotEqual(original, changed);
        Assert.Equal(original, extra);
    }

    [Theory]
    [InlineData("public class Importer", "internal class Importer")]
    [InlineData("bcc64662-e334-49b1-b559-55d89b06e351", "not-an-identity")]
    [InlineData("typeof(Data)", "typeof(string)")]
    [InlineData("typeof(Data)", "typeof(Data), typeof(Data)")]
    [InlineData("public Importer(Dependency dependency)", "private Importer(Dependency dependency)")]
    [InlineData("public int Number { get; }", "public int Number { get; } public Importer() { }")]
    public void UnavailableDeclarationsHaveActionableDiagnostics(string original, string replacement)
    {
        var result = Compile(ExtensionSource.Replace(original, replacement, StringComparison.Ordinal));
        Diagnostic error = Assert.Single(result.Diagnostics.Where(value => value.Id == "SEAC001"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Importer", error.GetMessage());
    }

    [Fact]
    public void ExplicitConstructorSelectionIsUsedByGeneratedServices()
    {
        string source = ExtensionSource.Replace("public Importer(Dependency dependency)",
            "public Importer() { Number = -1; } [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Importer(Dependency dependency)", StringComparison.Ordinal)
            + """

            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static bool Execute()
                {
                    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new Dependency());
                    ProjectAssets.AddAssetCapabilities(services);
                    using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
                    return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Importer>(provider).Number == 47;
                }
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source)))!);
    }

    [Fact]
    public void DuplicateImporterIdentityAcrossReferencedAssembliesIsRejected()
    {
        byte[] first = Emit(Compile(ExtensionSource));
        byte[] second = Emit(Compile(ExtensionSource.Replace("namespace Extension;", "namespace OtherExtension;", StringComparison.Ordinal)
            .Replace("example.extension-data", "example.other-extension-data", StringComparison.Ordinal)));
        var result = Compile("using SomeEngine.Assets; [AssetCapabilities] public static partial class ProjectAssets;",
            dependencies: [MetadataReference.CreateFromImage(first), MetadataReference.CreateFromImage(second)]);
        Assert.Contains(result.Diagnostics, value => value.Id == "SEAC002"
            && value.GetMessage().Contains("bcc64662-e334-49b1-b559-55d89b06e351", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateBlockIdentityAcrossDifferentCodecsIsRejected()
    {
        string firstSource = PlainBlock("First", "Data", "example.shared-data");
        string secondSource = """
            using SomeEngine.Assets;
            using SomeEngine.Serialization;
            namespace Second;
            [AssetBlock("example.shared-data"), BinarySerializable, BinaryFields(nameof(Value))]
            public partial class Data : AssetBlock { public partial float Value { get; set; } }
            """;
        byte[] first = Emit(Compile(firstSource));
        byte[] second = Emit(Compile(secondSource));
        var result = Compile("using SomeEngine.Assets; [AssetCapabilities] public static partial class ProjectAssets;",
            dependencies: [MetadataReference.CreateFromImage(first), MetadataReference.CreateFromImage(second)]);
        Assert.Contains(result.Diagnostics, value => value.Id == "SEAC002"
            && value.GetMessage().Contains("First.Data", StringComparison.Ordinal)
            && value.GetMessage().Contains("Second.Data", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessOnlyBlockNeedsNoCodecAndHasAnIndependentGeneratedIdentity()
    {
        string source = PlainBlock("Extension", "Data", "example.process-data") + """

            public static class Consumer
            {
                public static bool Execute()
                {
                    using var block = new Data(31);
                    return block.Value == 31 && block.BlockId == Data.BlockTypeId
                        && (object)block is not SomeEngine.Assets.IAssetBlockWriter;
                }
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source)))!);
    }

    [Fact]
    public void EncodedBlockTypeIdentitySurvivesAClrRename()
    {
        const string source = """
            using System;
            using SomeEngine.Assets;
            using SomeEngine.Serialization;
            namespace Extension;
            [AssetBlock("example.rename-safe"), BinarySerializable, BinaryFields(nameof(Value))]
            public sealed partial class Data : AssetBlock { public partial int Value { get; set; } }
            public static class Consumer { public static Guid Execute() => Data.TypeId; }
            """;
        Guid before = (Guid)Execute(Emit(Compile(source)))!;
        Guid after = (Guid)Execute(Emit(Compile(source.Replace("Data", "RenamedData"))))!;
        Assert.Equal(before, after);
    }

    [Fact]
    public void BlockIdentityDoesNotDependOnCodecFieldsClrNameOrAssemblyName()
    {
        const string name = "example.persisted-data";
        string plain = PlainBlock("Original", "Data", name) + "\npublic static class Consumer { public static System.Guid Execute() => Data.BlockTypeId; }";
        string encoded = """
            using SomeEngine.Assets;
            using SomeEngine.Serialization;
            namespace Moved;
            [AssetBlock("example.persisted-data"), BinarySerializable, BinaryFields(nameof(Number), nameof(Label))]
            public partial class Renamed : AssetBlock
            {
                public partial int Number { get; set; }
                public partial string Label { get; set; }
            }
            public static class Consumer { public static System.Guid Execute() => Renamed.BlockTypeId; }
            """;
        Guid id = (Guid)Execute(Emit(Compile(plain)))!;
        Assert.Equal(id, (Guid)Execute(Emit(Compile(plain)))!);
        Assert.Equal(id, (Guid)Execute(Emit(Compile(encoded)))!);
        byte[] expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("SomeEngine.AssetBlock.v1:" + name));
        Assert.Equal(new Guid(expected.AsSpan(0, 16), bigEndian: true), id);
    }

    [Fact]
    public void BlockIdentityAndEncodingIdentityAreDifferent()
    {
        string source = ExtensionSource + """

            public static class Consumer
            {
                public static bool Execute() => Data.BlockTypeId != Data.TypeId;
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source)))!);
    }

    [Fact]
    public async Task DifferentProjectCapabilitiesPreserveUnknownBlocksWhenSavingAgain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "asset-block-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "first.mesh.asset");
        string second = Path.Combine(directory, "second.mesh.asset");
        try
        {
            const string writer = """
                using System.Linq;
                using System.Threading.Tasks;
                using SomeEngine.Assets;
                using SomeEngine.Assets.Schema;
                using SomeEngine.Serialization;
                [AssetBlock("example.cross-project-extra"), BinarySerializable, BinaryFields(nameof(Value), nameof(Dependency))]
                public sealed partial class ExtraData : AssetBlock
                {
                    public partial int Value { get; set; }
                    public partial AssetId<TextureAsset> Dependency { get; set; }
                }
                [AssetCapabilities] public static partial class ProjectAssets;
                public static class Consumer
                {
                    public static async Task<bool> Write(string path)
                    {
                        using var asset = new MeshAsset { Name = "first" };
                        using var mesh = new MeshBlock();
                        using var extra = new ExtraData
                        {
                            Value = 73,
                            Dependency = new AssetId<TextureAsset>(new System.Guid("f4cd60ea-3404-47b5-9828-82f039fdb179")),
                        };
                        asset.SetBlock(extra);
                        asset.SetBlock(mesh);
                        await asset.SaveAsync(path);
                        using MeshAsset reopened = await MeshAsset.OpenAsync(path);
                        string[] ids = reopened.BlockDescriptions.Select(value => value.BlockTypeId.ToString("N")).ToArray();
                        return ids.SequenceEqual(ids.OrderBy(value => value, System.StringComparer.Ordinal));
                    }
                    public static async Task<bool> Verify(string path)
                    {
                        using MeshAsset asset = await MeshAsset.OpenAsync(path);
                        ExtraData extra = await asset.GetBlockAsync<ExtraData>();
                        var dependency = new System.Guid("f4cd60ea-3404-47b5-9828-82f039fdb179");
                        return asset.Name == "second" && asset.ContainsBlock<MeshBlock>() && extra.Value == 73
                            && extra.Dependency.Value == dependency && asset.Describe().Dependencies.Contains(dependency);
                    }
                }
                """;
            const string reader = """
                using System.Threading.Tasks;
                using SomeEngine.Assets;
                using SomeEngine.Assets.Schema;
                [AssetCapabilities] public static partial class ProjectAssets;
                public static class Consumer
                {
                    public static async Task<bool> Rewrite(string source, string destination)
                    {
                        using MeshAsset asset = await MeshAsset.OpenAsync(source);
                        if (!asset.ContainsBlock<MeshBlock>()) return false;
                        asset.Name = "second";
                        await asset.SaveAsync(destination);
                        return true;
                    }
                }
                """;
            byte[] writerBytes = Emit(CompileWithAssetAssemblyOnly(writer));
            byte[] readerBytes = Emit(CompileWithAssetAssemblyOnly(reader));
            Assert.True(await ExecuteWithSeparateAssetsAsync(writerBytes, "Write", first));
            Assert.True(await ExecuteWithSeparateAssetsAsync(readerBytes, "Rewrite", first, second));
            Assert.True(await ExecuteWithSeparateAssetsAsync(writerBytes, "Verify", second));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RegistrationRequiresAnAccessibleStaticPartialClass()
    {
        var result = Compile("using SomeEngine.Assets; [AssetCapabilities] internal partial class ProjectAssets;");
        Assert.Contains(result.Diagnostics, value => value.Id == "SEAC003");
    }

    private static string PlainBlock(string space, string type, string name) => $$"""
        using SomeEngine.Assets;
        using SomeEngine.Serialization.Containers;
        namespace {{space}};
        [AssetBlock("{{name}}")]
        public sealed partial class {{type}} : AssetBlock
        {
            public int Value { get; }
            public {{type}}(int value) => Value = value;
            private {{type}}({{type}} source, object? syncRoot = null) : base(source, syncRoot) => Value = source.Value;
            public override AssetBlock Duplicate() => new {{type}}(this);
            public override AssetBlock Duplicate(object syncRoot) => new {{type}}(this, syncRoot);
        }
        """;

    private static object? Execute(byte[] bytes, params byte[][] dependencies)
    {
        var load = new AssemblyLoadContext("AssetConsumer-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            foreach (byte[] dependency in dependencies)
            {
                using var input = new MemoryStream(dependency);
                load.LoadFromStream(input);
            }
            using var stream = new MemoryStream(bytes);
            Assembly assembly = load.LoadFromStream(stream);
            // The generated consumer's public entry is the behavior under test, not engine internals.
            Type consumer = assembly.GetExportedTypes().Single(type => type.Name == "Consumer");
            return consumer.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }
        finally { load.Unload(); }
    }

    private static async Task<bool> ExecuteWithSeparateAssetsAsync(byte[] bytes, string method, params string[] arguments)
    {
        var load = new SeparateAssetsContext();
        try
        {
            using var stream = new MemoryStream(bytes);
            Assembly assembly = load.LoadFromStream(stream);
            Type consumer = assembly.GetExportedTypes().Single(type => type.Name == "Consumer");
            var task = (Task<bool>)consumer.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, arguments.Cast<object>().ToArray())!;
            return await task;
        }
        finally { load.Unload(); }
    }

    private sealed class SeparateAssetsContext : AssemblyLoadContext
    {
        internal SeparateAssetsContext() : base("SeparateAssets-" + Guid.NewGuid(), isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName)
            => AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(Asset).Assembly.GetName())
                ? LoadFromAssemblyPath(typeof(Asset).Assembly.Location)
                : null;
    }

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Compile(string source,
        bool generators = true, bool capabilities = true, params MetadataReference[] dependencies)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(Asset).Assembly.Location, typeof(BinarySerializableAttribute).Assembly.Location,
                typeof(IServiceCollection).Assembly.Location, typeof(ServiceCollectionContainerBuilderExtensions).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().Concat(dependencies);
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("AssetExtension" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        if (!generators) return (compilation, []);
        var selected = new List<ISourceGenerator> { new BinaryContractGenerator().AsSourceGenerator(), new AssetBlockGenerator().AsSourceGenerator() };
        if (capabilities) selected.Add(new AssetCapabilitiesGenerator().AsSourceGenerator());
        GeneratorDriver driver = CSharpGeneratorDriver.Create(selected, parseOptions: parse);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (output, diagnostics);
    }

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) CompileWithAssetAssemblyOnly(string source)
    {
        string[] enginePrefixes = ["SomeEngine.", "Game."];
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !enginePrefixes.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .GroupBy(static assembly => assembly.GetName().Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Location, StringComparer.OrdinalIgnoreCase);
        var assetDependencies = typeof(Asset).Assembly.GetReferencedAssemblies()
            .Select(name => loaded.TryGetValue(name.Name!, out string? path) ? path : null)
            .Where(static path => path is not null)!;
        var paths = framework.Concat(assetDependencies!)
            .Concat([typeof(Asset).Assembly.Location, typeof(BinarySerializableAttribute).Assembly.Location,
                typeof(IServiceCollection).Assembly.Location, typeof(ServiceCollectionContainerBuilderExtensions).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path!));
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("AssetProject" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new BinaryContractGenerator().AsSourceGenerator(), new AssetBlockGenerator().AsSourceGenerator(),
             new AssetCapabilitiesGenerator().AsSourceGenerator()], parseOptions: parse);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (output, diagnostics);
    }

    private static byte[] Emit((Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) result)
    {
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var output = result.Compilation.Emit(stream);
        Assert.True(output.Success, string.Join(Environment.NewLine, output.Diagnostics));
        return stream.ToArray();
    }
}

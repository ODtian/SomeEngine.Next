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
    [Fact]
    public void ImporterConstructorServicesProduceACompilerDiagnostic()
    {
        string source = ExtensionSource.Replace("public Importer() { }",
            "public Importer(Dependency dependency) { _ = dependency.Number; }", StringComparison.Ordinal)
            + "\npublic sealed class Dependency { public int Number => 47; }";
        var result = Compile(source);
        Assert.Contains(result.Diagnostics, value => value.Id == "SEAC004"
            && value.Severity == DiagnosticSeverity.Error && value.GetMessage().Contains("Extension.Importer", StringComparison.Ordinal));
    }

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
        [AssetImporter("bcc64662-e334-49b1-b559-55d89b06e351", typeof(Data))]
        public class Importer : IAssetImporter<Source, Settings>
        {
            public Importer() { }
            public int Number => 47;
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
        string original = (string)Execute(Emit(Compile(ExtensionSource + read, assemblyName: "VersionedExtension")))!;
        string changed = (string)Execute(Emit(Compile(ExtensionSource.Replace("=> 47", "=> 48", StringComparison.Ordinal) + read, assemblyName: "VersionedExtension")))!;
        string extra = (string)Execute(Emit(Compile(ExtensionSource + read + "\n[AssetCapabilities] public static partial class ExtraRegistration;", assemblyName: "VersionedExtension")))!;
        Assert.NotEmpty(original);
        Assert.NotEqual(original, changed);
        Assert.Equal(original, extra);
    }

    private const string VersionedBlockSource = """
        using SomeEngine.Assets;
        using SomeEngine.Serialization;
        namespace BlockLibrary;
        [AssetBlock("example.versioned-block"), BinarySerializable, BinaryFields(nameof(Value))]
        public sealed partial class Data : AssetBlock
        {
            public partial int Value { get; set; }
            public static int InitialValue => ValueBuilder.Read();
        }
        internal static class ValueBuilder { public static int Read() => 47; }
        public static class ShaderCompiler { public static int Revision => 1; }
        """;

    private const string ExternalBlockImporterSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using SomeEngine.Assets;
        using SomeEngine.Assets.Importing;
        namespace Extension;
        public sealed class Source;
        public sealed record Settings(int Value);
        [AssetImporter("bcc64662-e334-49b1-b559-55d89b06e351", typeof(BlockLibrary.Data))]
        public sealed class Importer : IAssetImporter<Source, Settings>
        {
            public ValueTask ImportAsync(Source source, Settings settings, ImportContext context,
                ImportOutput output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
        [AssetCapabilities] public static partial class LibraryAssets;
        public static class Consumer
        {
            public static string Execute() => System.Linq.Enumerable.Single(LibraryAssets.ImporterCapabilities,
                value => value.ImporterType == typeof(Importer)).ImplementationVersion;
        }
        """;

    [Fact]
    public void BuiltinPointerArithmeticDoesNotAcquireAnUnrelatedImplementationBody()
    {
        string source = VersionedBlockSource.Replace("public static int Read() => 47;", """
            public static unsafe int Read()
            {
                byte* values = stackalloc byte[2];
                values[0] = 47;
                values[1] = 11;
                byte* cursor = values + 1;
                return *(cursor - 1);
            }
            """, StringComparison.Ordinal);
        string version = ImporterVersionForBlocks(source);
        Assert.Equal(version, ImporterVersionForBlocks(source.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal)));
        Assert.NotEqual(version, ImporterVersionForBlocks(source.Replace("values[0] = 47", "values[0] = 48", StringComparison.Ordinal)));
    }

    [Fact]
    public void FunctionPointerInvocationEmitsItsExplicitConservativeImplementationContract()
    {
        const string source = """
            public static unsafe class NativeCalls
            {
                public static int Invoke(delegate*<int, int> callback, int value) => callback(value);
            }
            """;
        var result = Compile(source);
        Emit(result);
        AttributeData metadata = Assert.Single(result.Compilation.Assembly.GetAttributes().Where(value =>
            value.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"
            && value.ConstructorArguments.Length == 2
            && value.ConstructorArguments[0].Value as string == "SomeEngine.ImplementationInputs.v1:NativeCalls"));
        string encoded = Assert.IsType<string>(metadata.ConstructorArguments[1].Value);
        Assert.Equal("1", encoded.Split('\n')[1]);
    }

    [Fact]
    public void NameofACompilerMemberDoesNotDependOnItsImplementation()
    {
        string source = VersionedBlockSource.Replace("public static int InitialValue", "public static string CompilerName => nameof(ShaderCompiler.Revision); public static int InitialValue", StringComparison.Ordinal);
        Assert.Equal(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void SizeofTracksNestedValueLayout()
    {
        string source = VersionedBlockSource.Replace("public static int Read() => 47", "public static unsafe int Read() => sizeof(Layout)", StringComparison.Ordinal)
            + "\npublic struct Layout { public Nested Value; } public struct Nested { public int Value; }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("struct Nested { public int Value; }", "struct Nested { public long Value; }", StringComparison.Ordinal)));
    }

    [Fact]
    public void WithExpressionsTrackTheSuppliedRecordsCopyImplementation()
    {
        string source = VersionedBlockSource.Replace("Read() => 47", "Read(Value value = null!) => (value with { }).Data", StringComparison.Ordinal)
            + "\npublic record Value { public int Data { get; init; } public Value() { } protected Value(Value source) { Data = source.Data + 1; } }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("source.Data + 1", "source.Data + 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void ImplicitIndexingTracksLengthAndIndexerImplementations()
    {
        string source = VersionedBlockSource.Replace("Read() => 47", "Read(Buffer value = null!) => value[^1]", StringComparison.Ordinal)
            + "\npublic sealed class Buffer { public int Length => 1; public int this[int index] => 47; }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("this[int index] => 47", "this[int index] => 48", StringComparison.Ordinal)));
    }

    [Fact]
    public void CollectionExpressionsTrackTheirSelectedBuilderImplementation()
    {
        string source = VersionedBlockSource.Replace("Read() => 47;", "Read() { Values values = [1]; return values.Count; }", StringComparison.Ordinal) + """

            [System.Runtime.CompilerServices.CollectionBuilder(typeof(ValuesBuilder), nameof(ValuesBuilder.Create))]
            public readonly struct Values : System.Collections.Generic.IEnumerable<int>
            {
                private readonly int[] _items;
                public Values(int[] items) => _items = items;
                public int Count => _items.Length;
                public System.Collections.Generic.IEnumerator<int> GetEnumerator() => ((System.Collections.Generic.IEnumerable<int>)_items).GetEnumerator();
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class ValuesBuilder
            {
                public static Values Create(System.ReadOnlySpan<int> values) => new(new[] { 47 });
            }
            """;
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("new[] { 47 }", "new[] { 48 }", StringComparison.Ordinal)));
    }

    [Fact]
    public void APhantomCodecTypeArgumentDoesNotDependOnItsRuntimeImplementation()
    {
        Assert.Equal(ImporterVersionForBlocks(TypedIdBlockSource),
            ImporterVersionForBlocks(TypedIdBlockSource.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void AGeneratedFieldStillDependsOnItsHandwrittenCodecBody()
    {
        Assert.NotEqual(ImporterVersionForBlocks(TypedIdBlockSource),
            ImporterVersionForBlocks(TypedIdBlockSource.Replace("writer.WriteGuid(value.Value)", "writer.WriteGuid(System.Guid.Empty)", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("SchemaFingerprint => 17", "SchemaFingerprint => 18")]
    [InlineData("SchemaEpoch => 1", "SchemaEpoch => 2")]
    [InlineData("example.typed-id:", "example.changed-typed-id:")]
    [InlineData("=> BinaryCompatibility.ExactSchema", "=> (BinaryCompatibility)1")]
    public void AGeneratedFieldDependsOnItsCompleteCodecDescriptor(string before, string after)
    {
        Assert.NotEqual(ImporterVersionForBlocks(TypedIdBlockSource),
            ImporterVersionForBlocks(TypedIdBlockSource.Replace(before, after, StringComparison.Ordinal)));
    }

    [Fact]
    public void ExplicitStaticCodecImplementationsRemainImplementationInputs()
    {
        string source = TypedIdBlockSource
            .Replace("public static void Write(", "static void IBinarySerializable<TypedId<T>>.Write(", StringComparison.Ordinal)
            .Replace("public static TypedId<T> Read(", "static TypedId<T> IBinarySerializable<TypedId<T>>.Read(", StringComparison.Ordinal);
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("writer.WriteGuid(value.Value)", "writer.WriteGuid(System.Guid.Empty)", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitStaticCodecsRoundTripAndValidateNestedValues(bool referenceCodec)
    {
        string source = TypedIdBlockSource
            .Replace("[AssetBlock(\"example.versioned-block\"), BinarySerializable, BinaryFields(nameof(Value))]",
                "[BinarySerializable, BinaryFields(nameof(Value), nameof(Optional), nameof(Items))]", StringComparison.Ordinal)
            .Replace("public sealed partial class Data : AssetBlock { public partial TypedId<ShaderAsset> Value { get; set; } }",
                "public sealed partial class Data { public TypedId<ShaderAsset> Value { get; set; } = default!; public TypedId<ShaderAsset>? Optional { get; set; } public TypedId<ShaderAsset>[] Items { get; set; } = []; }", StringComparison.Ordinal)
            .Replace("public static System.Guid TypeId", "static System.Guid IBinarySerializable<TypedId<T>>.TypeId", StringComparison.Ordinal)
            .Replace("public static ulong SchemaFingerprint", "static ulong IBinarySerializable<TypedId<T>>.SchemaFingerprint", StringComparison.Ordinal)
            .Replace("public static uint SchemaEpoch", "static uint IBinarySerializable<TypedId<T>>.SchemaEpoch", StringComparison.Ordinal)
            .Replace("public static BinaryCompatibility Compatibility", "static BinaryCompatibility IBinarySerializable<TypedId<T>>.Compatibility", StringComparison.Ordinal)
            .Replace("public static void Write(ref BinaryDataWriter writer, TypedId<T> value) => writer.WriteGuid(value.Value);",
                "static void IBinarySerializable<TypedId<T>>.Write(ref BinaryDataWriter writer, TypedId<T> value) { CodecCalls.Writes++; writer.WriteGuid(value.Value); }", StringComparison.Ordinal)
            .Replace("public static TypedId<T> Read(ref BinaryDataReader reader) => new(reader.ReadGuid());",
                "static TypedId<T> IBinarySerializable<TypedId<T>>.Read(ref BinaryDataReader reader) { CodecCalls.Reads++; return new(reader.ReadGuid()); }", StringComparison.Ordinal)
            .Replace("public static void ValidateView(ref BinaryViewReader reader) => reader.ReadGuid();",
                "static void IBinaryCustomViewContract<TypedId<T>>.ValidateView(ref BinaryViewReader reader) { CodecCalls.Validations++; reader.ReadGuid(); }", StringComparison.Ordinal);
        if (referenceCodec)
            source = source.Replace("public readonly struct TypedId<T>", "public sealed class TypedId<T>", StringComparison.Ordinal);
        source += """

            public static class CodecCalls { public static int Writes; public static int Reads; public static int Validations; }
            public static class Consumer
            {
                public static bool Execute()
                {
                    var first = new System.Guid("89124bfe-7503-4d7b-b108-1ee0063574aa");
                    var second = new System.Guid("19b6cbef-8070-4377-930a-7aaf398f8f50");
                    var value = new Data
                    {
                        Value = new TypedId<ShaderAsset>(first),
                        Optional = new TypedId<ShaderAsset>(second),
                        Items = [new TypedId<ShaderAsset>(first), new TypedId<ShaderAsset>(second)],
                    };
                    var bytes = new System.Buffers.ArrayBufferWriter<byte>();
                    BinarySerializer.Write(bytes, value);
                    Data.ValidateCanonical(bytes.WrittenSpan);
                    Data decoded = BinarySerializer.Deserialize<Data>(bytes.WrittenSpan);
                    var optional = decoded.Optional ?? throw new System.InvalidOperationException("Optional codec value was lost.");
                    BinaryContractDescriptor descriptor = BinaryContract<TypedId<ShaderAsset>>.Descriptor;
                    return decoded.Value.Value == first && optional.Value == second
                        && decoded.Items.Length == 2 && decoded.Items[0].Value == first && decoded.Items[1].Value == second
                        && CodecCalls.Writes == 4 && CodecCalls.Reads == 4 && CodecCalls.Validations == 4
                        && descriptor.TypeId != System.Guid.Empty && descriptor.SchemaFingerprint == 17
                        && descriptor.SchemaEpoch == 1 && descriptor.Compatibility == BinaryCompatibility.ExactSchema;
                }
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source)))!);
    }

    [Fact]
    public void PublicConstantsAndDefaultValuesRemainImplementationInputs()
    {
        string source = VersionedBlockSource.Replace("Read() => 47", "Read(int value = Defaults.Value) => value", StringComparison.Ordinal)
            + "\npublic static class Defaults { public const int Value = 47; }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("const int Value = 47", "const int Value = 48", StringComparison.Ordinal)));
    }

    [Fact]
    public void InlineValueStorageDependsOnNestedValueLayout()
    {
        string source = VersionedBlockSource.Replace("Read() => 47", "Read() => System.Runtime.CompilerServices.Unsafe.SizeOf<Layout>()", StringComparison.Ordinal)
            + "\npublic struct Layout { public Nested Value; } public struct Nested { public int Value; }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("struct Nested { public int Value; }", "struct Nested { public long Value; }", StringComparison.Ordinal)));
    }

    [Fact]
    public void AGenericCodecTracksTheConcreteCodecItInvokes()
    {
        string source = TypedIdBlockSource.Replace("TypedId<ShaderAsset> Value", "Forward<TypedId<ShaderAsset>> Value", StringComparison.Ordinal) + """

            public readonly struct Forward<T> : IBinarySerializable<Forward<T>>, IBinaryCustomViewContract<Forward<T>>
                where T : IBinarySerializable<T>, IBinaryCustomViewContract<T>
            {
                public Forward(T value) => Value = value;
                public T Value { get; }
                public static System.Guid TypeId => BinaryTypeId.FromLogicalName("example.forward:" + typeof(T).FullName);
                public static ulong SchemaFingerprint => 71;
                public static uint SchemaEpoch => 1;
                public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
                public static void Write(ref BinaryDataWriter writer, Forward<T> value) => T.Write(ref writer, value.Value);
                public static Forward<T> Read(ref BinaryDataReader reader) => new(T.Read(ref reader));
                public static void ValidateView(ref BinaryViewReader reader) => T.ValidateView(ref reader);
            }
            """;
        string version = ImporterVersionForBlocks(source);
        Assert.Equal(version, ImporterVersionForBlocks(source.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal)));
        Assert.NotEqual(version, ImporterVersionForBlocks(source.Replace("writer.WriteGuid(value.Value)", "writer.WriteGuid(System.Guid.Empty)", StringComparison.Ordinal)));
    }

    [Fact]
    public void GeneratedEnumEncodingDependsOnItsDeclaredValues()
    {
        string source = VersionedBlockSource.Replace("partial int Value", "partial Mode Value", StringComparison.Ordinal)
            + "\npublic enum Mode { First = 1, Second = 2 }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("Second = 2", "Second = 3", StringComparison.Ordinal)));
    }

    [Fact]
    public void UserDefinedImplicitConversionBodiesAreImplementationInputs()
    {
        string source = VersionedBlockSource.Replace("Read() => 47", "Read() => default(Converted)", StringComparison.Ordinal)
            + "\npublic struct Converted { public static implicit operator int(Converted value) => 47; }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("operator int(Converted value) => 47", "operator int(Converted value) => 48", StringComparison.Ordinal)));
    }

    [Fact]
    public void UsingAnExternallySuppliedConcreteResourceTracksItsDisposalImplementation()
    {
        string source = VersionedBlockSource.Replace("Read() => 47;", "Read(Helper value = null!) { using (value) { return 47; } }", StringComparison.Ordinal)
            + "\npublic sealed class Helper : System.IDisposable { public void Dispose() => System.GC.KeepAlive(1); }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("KeepAlive(1)", "KeepAlive(2)", StringComparison.Ordinal)));
    }

    [Fact]
    public void AwaitUsingAnExternallySuppliedConcreteResourceTracksItsDisposalImplementation()
    {
        string source = VersionedBlockSource.Replace("Read() => 47;", "Read() => ReadCore(null!).GetAwaiter().GetResult(); public static async System.Threading.Tasks.ValueTask<int> ReadCore(Helper value) { await using (value) { return 47; } }", StringComparison.Ordinal)
            + "\npublic sealed class Helper : System.IAsyncDisposable { public System.Threading.Tasks.ValueTask DisposeAsync() { System.GC.KeepAlive(1); return System.Threading.Tasks.ValueTask.CompletedTask; } }";
        Assert.NotEqual(ImporterVersionForBlocks(source),
            ImporterVersionForBlocks(source.Replace("KeepAlive(1)", "KeepAlive(2)", StringComparison.Ordinal)));
    }

    [Fact]
    public void DeclaredImplementationInputsExcludeOtherTypesInTheSameLibrary()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        const string algorithms = "namespace Algorithms; public static class Conversion { public static int Value => 1; } public static class ShaderCompiler { public static int Revision => 1; }";
        byte[] original = Emit(Compile(algorithms, assemblyName: "Algorithms"));
        byte[] unrelatedChange = Emit(Compile(algorithms.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal), assemblyName: "Algorithms"));
        string importerSource = ExternalBlockImporterSource.Replace("typeof(BlockLibrary.Data))]",
            "typeof(BlockLibrary.Data), VersionInputs = new[] { typeof(Algorithms.Conversion) })]", StringComparison.Ordinal);
        byte[] first = Emit(Compile(importerSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(original)]));
        byte[] second = Emit(Compile(importerSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(unrelatedChange)]));
        Assert.Equal((string)Execute(first, blocks, original)!, (string)Execute(second, blocks, unrelatedChange)!);
    }

    [Fact]
    public void MissingImplementationMetadataRetainsAnAssemblyFallbackForDeclaredInputs()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        const string algorithms = "[assembly: System.Reflection.AssemblyMetadata(\"SomeEngine.SourceBuild\", \"algorithms-1\")] namespace Algorithms { public static class Conversion { public static int Value => 1; } }";
        byte[] original = Emit(Compile(algorithms, capabilities: false, assemblyName: "Algorithms"));
        byte[] changed = Emit(Compile(algorithms.Replace("algorithms-1", "algorithms-2", StringComparison.Ordinal), capabilities: false, assemblyName: "Algorithms"));
        string importerSource = ExternalBlockImporterSource.Replace("typeof(BlockLibrary.Data))]",
            "typeof(BlockLibrary.Data), VersionInputs = new[] { typeof(Algorithms.Conversion) })]", StringComparison.Ordinal);
        byte[] first = Emit(Compile(importerSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(original)]));
        byte[] second = Emit(Compile(importerSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(changed)]));
        Assert.NotEqual((string)Execute(first, blocks, original)!, (string)Execute(second, blocks, changed)!);
    }

    [Fact]
    public void TruncatedImplementationMetadataFallsBackToTheAssemblyVersion()
    {
        string malformed = new string('0', 64) + "\n0\nN1:a1:b1:";
        string attributes = "[assembly: System.Reflection.AssemblyMetadata(\"SomeEngine.SourceBuild\", \"block-version-1\")]\n"
            + "[assembly: System.Reflection.AssemblyMetadata(\"SomeEngine.ImplementationInputs.v1:BlockLibrary.Data\", "
            + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(malformed, quote: true) + ")]\n";
        string source = VersionedBlockSource.Replace("namespace BlockLibrary;", attributes + "namespace BlockLibrary;", StringComparison.Ordinal);
        byte[] firstBlocks = Emit(Compile(source, capabilities: false, assemblyName: "BlockLibrary"));
        byte[] secondBlocks = Emit(Compile(source.Replace("block-version-1", "block-version-2", StringComparison.Ordinal), capabilities: false, assemblyName: "BlockLibrary"));
        byte[] first = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary", dependencies: [MetadataReference.CreateFromImage(firstBlocks)]));
        byte[] second = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary", dependencies: [MetadataReference.CreateFromImage(secondBlocks)]));
        Assert.NotEqual((string)Execute(first, firstBlocks)!, (string)Execute(second, secondBlocks)!);
    }

    [Fact]
    public void ExternAliasedVersionsDoNotCollapseToTheSameImplementationInput()
    {
        const string algorithm = "namespace Algorithms; public static class Conversion { public static int Value => 1; }";
        const string firstIdentity = "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] ";
        const string secondIdentity = "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")] ";
        ImmutableArray<byte> publicKey = ImmutableArray.CreateRange(typeof(object).Assembly.GetName().GetPublicKey()!);
        Assert.False(publicKey.IsDefaultOrEmpty);
        byte[] firstAlgorithm = Emit(Compile(firstIdentity + algorithm, assemblyName: "Algorithms", publicKey: publicKey));
        byte[] changedAlgorithm = Emit(Compile(firstIdentity + algorithm.Replace("Value => 1", "Value => 2", StringComparison.Ordinal), assemblyName: "Algorithms", publicKey: publicKey));
        byte[] secondAlgorithm = Emit(Compile(secondIdentity + algorithm.Replace("Value => 1", "Value => 99", StringComparison.Ordinal), assemblyName: "Algorithms", publicKey: publicKey));
        var firstReference = MetadataReference.CreateFromImage(firstAlgorithm).WithAliases(["First"]);
        var changedReference = MetadataReference.CreateFromImage(changedAlgorithm).WithAliases(["First"]);
        var secondReference = MetadataReference.CreateFromImage(secondAlgorithm).WithAliases(["Second"]);
        string source = "extern alias First; extern alias Second;\n" + VersionedBlockSource.Replace("ValueBuilder.Read()", "First::Algorithms.Conversion.Value", StringComparison.Ordinal);
        byte[] firstBlocks = Emit(Compile(source, assemblyName: "BlockLibrary", dependencies: [firstReference, secondReference]));
        byte[] secondBlocks = Emit(Compile(source, assemblyName: "BlockLibrary", dependencies: [changedReference, secondReference]));
        byte[] first = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary", dependencies: [MetadataReference.CreateFromImage(firstBlocks), firstReference, secondReference]));
        byte[] second = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary", dependencies: [MetadataReference.CreateFromImage(secondBlocks), changedReference, secondReference]));
        // Reading the generated public version does not execute the optional implementation.
        Assert.NotEqual((string)Execute(first, firstBlocks)!, (string)Execute(second, secondBlocks)!);
    }

    [Fact]
    public void MissingImplementationOnlyTransitiveReferencesHaveAnActionableDiagnostic()
    {
        byte[] algorithm = Emit(Compile("namespace Algorithms; public static class Conversion { public static int Value => 1; }", assemblyName: "Algorithms"));
        byte[] blocks = Emit(Compile(VersionedBlockSource.Replace("ValueBuilder.Read()", "Algorithms.Conversion.Value", StringComparison.Ordinal),
            assemblyName: "BlockLibrary", dependencies: [MetadataReference.CreateFromImage(algorithm)]));
        byte[] importer = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(algorithm)]));
        const string project = "using SomeEngine.Assets; [AssetCapabilities] public static partial class ProjectAssets;";
        var result = Compile(project, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(importer)]);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SEAC005" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private const string TypedIdBlockSource = """
        using SomeEngine.Assets;
        using SomeEngine.Serialization;
        namespace BlockLibrary;
        [AssetBlock("example.versioned-block"), BinarySerializable, BinaryFields(nameof(Value))]
        public sealed partial class Data : AssetBlock { public partial TypedId<ShaderAsset> Value { get; set; } }
        [BinarySerializable] public sealed partial class ShaderAsset { public static int Revision => 1; }
        public readonly struct TypedId<T> : IBinarySerializable<TypedId<T>>, IBinaryCustomViewContract<TypedId<T>>
        {
            public TypedId(System.Guid value) => Value = value;
            public System.Guid Value { get; }
            public static System.Guid TypeId => BinaryTypeId.FromLogicalName("example.typed-id:" + typeof(T).FullName);
            public static ulong SchemaFingerprint => 17;
            public static uint SchemaEpoch => 1;
            public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
            public static void Write(ref BinaryDataWriter writer, TypedId<T> value) => writer.WriteGuid(value.Value);
            public static TypedId<T> Read(ref BinaryDataReader reader) => new(reader.ReadGuid());
            public static void ValidateView(ref BinaryViewReader reader) => reader.ReadGuid();
        }
        """;

    private static string ImporterVersionForBlocks(string source)
    {
        byte[] blocks = Emit(Compile(source, assemblyName: "BlockLibrary"));
        byte[] importer = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks)]));
        return (string)Execute(importer, blocks)!;
    }

    [Fact]
    public void UnrelatedShaderImplementationDoesNotChangeAProducedBlocksImporterVersion()
    {
        byte[] originalBlocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(VersionedBlockSource.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal),
            assemblyName: "BlockLibrary"));
        byte[] originalImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        byte[] changedImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks)]));
        string original = (string)Execute(originalImporter, originalBlocks)!;
        string changed = (string)Execute(changedImporter, changedBlocks)!;
        Assert.NotEmpty(original);
        Assert.Equal(original, changed);
    }

    [Fact]
    public void AProducedBlocksHelperImplementationChangesItsImporterVersion()
    {
        byte[] originalBlocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(VersionedBlockSource.Replace("Read() => 47", "Read() => 48", StringComparison.Ordinal),
            assemblyName: "BlockLibrary"));
        byte[] originalImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        byte[] changedImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks)]));
        Assert.NotEqual((string)Execute(originalImporter, originalBlocks)!, (string)Execute(changedImporter, changedBlocks)!);
    }

    [Fact]
    public void AProducedBlocksEncodingFieldsChangeItsImporterVersion()
    {
        byte[] originalBlocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(VersionedBlockSource.Replace("partial int Value", "partial long Value", StringComparison.Ordinal),
            assemblyName: "BlockLibrary"));
        byte[] originalImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        byte[] changedImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks)]));
        Assert.NotEqual((string)Execute(originalImporter, originalBlocks)!, (string)Execute(changedImporter, changedBlocks)!);
    }

    [Fact]
    public void ShaderImplementationUsedByAProducedBlockStillChangesItsImporterVersion()
    {
        string source = VersionedBlockSource.Replace("ValueBuilder.Read()", "ShaderCompiler.Revision", StringComparison.Ordinal);
        byte[] originalBlocks = Emit(Compile(source, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(source.Replace("Revision => 1", "Revision => 2", StringComparison.Ordinal),
            assemblyName: "BlockLibrary"));
        byte[] originalImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        byte[] changedImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks)]));
        Assert.NotEqual((string)Execute(originalImporter, originalBlocks)!, (string)Execute(changedImporter, changedBlocks)!);
    }

    [Fact]
    public void ReferencedImporterHasTheSameVersionAsItsLibraryRegistration()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        byte[] importer = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks)]));
        const string source = """
            using SomeEngine.Assets;
            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static string Execute() => System.Linq.Enumerable.Single(ProjectAssets.ImporterCapabilities,
                    value => value.ImporterType == typeof(Extension.Importer)).ImplementationVersion;
            }
            """;
        byte[] project = Emit(Compile(source, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(importer)]));
        Assert.Equal((string)Execute(importer, blocks)!, (string)Execute(project, blocks, importer)!);
    }

    [Fact]
    public void ReferencedImporterUsesUpdatedProducedBlocksWithoutBeingRecompiled()
    {
        byte[] originalBlocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(VersionedBlockSource.Replace("Read() => 47", "Read() => 48", StringComparison.Ordinal),
            assemblyName: "BlockLibrary"));
        byte[] importer = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        const string source = """
            using SomeEngine.Assets;
            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static string Execute() => System.Linq.Enumerable.Single(ProjectAssets.ImporterCapabilities,
                    value => value.ImporterType == typeof(Extension.Importer)).ImplementationVersion;
            }
            """;
        byte[] originalProject = Emit(Compile(source, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks), MetadataReference.CreateFromImage(importer)]));
        byte[] changedProject = Emit(Compile(source, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks), MetadataReference.CreateFromImage(importer)]));
        Assert.NotEqual((string)Execute(originalProject, originalBlocks, importer)!,
            (string)Execute(changedProject, changedBlocks, importer)!);
    }

    [Fact]
    public void ExplicitVersionInputsTrackTheirImplementation()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        const string dependency = "namespace Algorithms; public static class Conversion { public static int Value => 1; }";
        byte[] originalAlgorithms = Emit(Compile(dependency, assemblyName: "Algorithms"));
        byte[] changedAlgorithms = Emit(Compile(dependency.Replace("Value => 1", "Value => 2", StringComparison.Ordinal),
            assemblyName: "Algorithms"));
        string source = ExternalBlockImporterSource.Replace("typeof(BlockLibrary.Data))]",
            "typeof(BlockLibrary.Data), VersionInputs = new[] { typeof(Algorithms.Conversion) })]", StringComparison.Ordinal);
        byte[] originalImporter = Emit(Compile(source, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(originalAlgorithms)]));
        byte[] changedImporter = Emit(Compile(source, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(changedAlgorithms)]));
        Assert.NotEqual((string)Execute(originalImporter, blocks, originalAlgorithms)!,
            (string)Execute(changedImporter, blocks, changedAlgorithms)!);
    }

    [Fact]
    public void DeclaredBuilderTracksDefaultsFromItsConfigurationType()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        const string dependency = """
            namespace Algorithms;
            public struct Configuration
            {
                public float Threshold;
                public static Configuration GetDefault() => new() { Threshold = .85f };
            }
            public static class Builder { public static float Read() => Configuration.GetDefault().Threshold; }
            """;
        byte[] originalAlgorithms = Emit(Compile(dependency, assemblyName: "Algorithms"));
        byte[] changedAlgorithms = Emit(Compile(dependency.Replace("Threshold = .85f", "Threshold = .75f", StringComparison.Ordinal),
            assemblyName: "Algorithms"));
        string importerSource = ExternalBlockImporterSource.Replace("typeof(BlockLibrary.Data))]",
            "typeof(BlockLibrary.Data), VersionInputs = new[] { typeof(Algorithms.Builder) })]", StringComparison.Ordinal);
        byte[] importer = Emit(Compile(importerSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(originalAlgorithms)]));
        const string source = """
            using SomeEngine.Assets;
            [AssetCapabilities] public static partial class ProjectAssets;
            public static class Consumer
            {
                public static string Execute() => System.Linq.Enumerable.Single(ProjectAssets.ImporterCapabilities,
                    value => value.ImporterType == typeof(Extension.Importer)).ImplementationVersion;
            }
            """;
        byte[] originalProject = Emit(Compile(source, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(originalAlgorithms),
                MetadataReference.CreateFromImage(importer)]));
        byte[] changedProject = Emit(Compile(source, assemblyName: "ProjectLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks), MetadataReference.CreateFromImage(changedAlgorithms),
                MetadataReference.CreateFromImage(importer)]));
        Assert.NotEqual((string)Execute(originalProject, blocks, originalAlgorithms, importer)!,
            (string)Execute(changedProject, blocks, changedAlgorithms, importer)!);
    }

    [Fact]
    public void ProducedBlocksWithoutTypeMetadataKeepTheirAssemblyVersionDependency()
    {
        string source = VersionedBlockSource.Replace("namespace BlockLibrary;",
            "[assembly: System.Reflection.AssemblyMetadata(\"SomeEngine.SourceBuild\", \"block-version-1\")]\nnamespace BlockLibrary;",
            StringComparison.Ordinal);
        byte[] originalBlocks = Emit(Compile(source, capabilities: false, assemblyName: "BlockLibrary"));
        byte[] changedBlocks = Emit(Compile(source.Replace("block-version-1", "block-version-2", StringComparison.Ordinal),
            capabilities: false, assemblyName: "BlockLibrary"));
        byte[] originalImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(originalBlocks)]));
        byte[] changedImporter = Emit(Compile(ExternalBlockImporterSource, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(changedBlocks)]));
        Assert.NotEqual((string)Execute(originalImporter, originalBlocks)!, (string)Execute(changedImporter, changedBlocks)!);
    }

    [Fact]
    public void ImporterLibraryHelperChangesRemainConservativeVersionInputs()
    {
        byte[] blocks = Emit(Compile(VersionedBlockSource, assemblyName: "BlockLibrary"));
        string source = ExternalBlockImporterSource + "\ninternal static class ImportHelper { public static int Value => 1; }";
        byte[] originalImporter = Emit(Compile(source, assemblyName: "ImporterLibrary",
            dependencies: [MetadataReference.CreateFromImage(blocks)]));
        byte[] changedImporter = Emit(Compile(source.Replace("Value => 1", "Value => 2", StringComparison.Ordinal),
            assemblyName: "ImporterLibrary", dependencies: [MetadataReference.CreateFromImage(blocks)]));
        Assert.NotEqual((string)Execute(originalImporter, blocks)!, (string)Execute(changedImporter, blocks)!);
    }

    [Theory]
    [InlineData("public class Importer", "internal class Importer")]
    [InlineData("bcc64662-e334-49b1-b559-55d89b06e351", "not-an-identity")]
    [InlineData("typeof(Data)", "typeof(string)")]
    [InlineData("typeof(Data)", "typeof(Data), typeof(Data)")]
    [InlineData("public Importer()", "private Importer()")]
    public void UnavailableDeclarationsHaveActionableDiagnostics(string original, string replacement)
    {
        var result = Compile(ExtensionSource.Replace(original, replacement, StringComparison.Ordinal));
        Diagnostic error = Assert.Single(result.Diagnostics.Where(value => value.Id == "SEAC001"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Importer", error.GetMessage());
    }

    [Theory]
    [InlineData("public Importer(int number) { }")]
    [InlineData("public Importer(System.IServiceProvider services) { }")]
    [InlineData("public Importer(int number = 47) { }")]
    [InlineData("public Importer(ref int number) { }")]
    [InlineData("public Importer() { } private Importer(int number) { }")]
    [InlineData("public Importer() { } [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Importer(int number) { }")]
    public void ParameterizedImporterConstructorsProduceCompilationErrors(string constructor)
    {
        var result = Compile(ExtensionSource.Replace("public Importer() { }", constructor, StringComparison.Ordinal));
        Diagnostic error = Assert.Single(result.Diagnostics.Where(value => value.Id == "SEAC004"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Extension.Importer", error.GetMessage());
        Assert.Contains("ImportContext", error.GetMessage());
        Assert.True(error.Location.IsInSource);
    }

    [Fact]
    public void ReferencedParameterizedImporterProducesACompilationError()
    {
        byte[] extension = Emit(Compile(ExtensionSource.Replace("public Importer() { }",
            "public Importer(System.IServiceProvider services) { }", StringComparison.Ordinal), capabilities: false));
        var result = Compile("using SomeEngine.Assets; [AssetCapabilities] public static partial class ProjectAssets;",
            dependencies: [MetadataReference.CreateFromImage(extension)]);
        Diagnostic error = Assert.Single(result.Diagnostics.Where(value => value.Id == "SEAC004"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Extension.Importer", error.GetMessage());
    }

    [Fact]
    public void PrimaryImporterConstructorProducesACompilationError()
    {
        string source = ExtensionSource.Replace("public class Importer :", "public class Importer(int number) :", StringComparison.Ordinal)
            .Replace("public Importer() { }", "", StringComparison.Ordinal);
        Diagnostic error = Assert.Single(Compile(source).Diagnostics.Where(value => value.Id == "SEAC004"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
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

    [Fact]
    public void ReferencedImporterSetsAreRegisteredAndProjectDefaultsMergeInEitherOrder()
    {
        const string set = """

            [AssetImporterSet("96c30d2f-6b98-4a48-a9f1-d2451d48b917", "Extension data", typeof(Importer), Neutral = true)]
            [AssetImporterSet("05f5a0d7-a030-48e9-b775-b07ed72b55ab", "Extension pipeline", typeof(Importer))]
            public static class Sets;
            """;
        byte[] extension = Emit(Compile(ExtensionSource + set, capabilities: false));
        const string source = """
            using System;
            using System.Linq;
            using Microsoft.Extensions.DependencyInjection;
            using SomeEngine.Assets;
            [AssetCapabilities("05f5a0d7-a030-48e9-b775-b07ed72b55ab")] public static partial class ProjectAssets;
            [AssetCapabilities] public static partial class LibraryAssets;
            public static class Consumer
            {
                public static bool Execute()
                {
                    foreach (bool projectFirst in new[] { false, true })
                    {
                        var services = new ServiceCollection();
                        if (projectFirst) ProjectAssets.AddAssetCapabilities(services);
                        LibraryAssets.AddAssetCapabilities(services);
                        ProjectAssets.AddAssetCapabilities(services);
                        ProjectAssets.AddAssetCapabilities(services);
                        using var provider = services.BuildServiceProvider();
                        var sets = provider.GetServices<AssetImporterSetCapability>().Where(value =>
                            value.Importers.Contains(new Guid("bcc64662-e334-49b1-b559-55d89b06e351"))).ToArray();
                        if (sets.Length != 2 || sets.Single(value => value.Neutral).Name != "Extension data"
                            || !sets.Single(value => !value.Neutral).Default) return false;
                    }
                    return true;
                }
            }
            """;
        Assert.True((bool)Execute(Emit(Compile(source, dependencies: [MetadataReference.CreateFromImage(extension)])), extension)!);
    }

    [Theory]
    [InlineData("bad-id", "typeof(Importer)")]
    [InlineData("96c30d2f-6b98-4a48-a9f1-d2451d48b917", "typeof(string)")]
    [InlineData("96c30d2f-6b98-4a48-a9f1-d2451d48b917", "typeof(Importer), typeof(Importer)")]
    public void InvalidImporterSetsProduceActionableCompilationErrors(string id, string members)
    {
        string declaration = $"\n[AssetImporterSet(\"{id}\", \"Extension data\", {members})] public static class Sets;";
        Assert.Contains(Compile(ExtensionSource + declaration).Diagnostics, value => value.Id == "SEAC001"
            && value.Severity == DiagnosticSeverity.Error && value.GetMessage().Contains("Sets", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateImporterSetIdentityProducesACompilationError()
    {
        const string source = """

            [AssetImporterSet("96c30d2f-6b98-4a48-a9f1-d2451d48b917", "First", typeof(Importer))]
            [AssetImporterSet("96c30d2f-6b98-4a48-a9f1-d2451d48b917", "Second", typeof(Importer))]
            public static class Sets;
            """;
        Assert.Contains(Compile(ExtensionSource + source).Diagnostics, value => value.Id == "SEAC002");
    }

    [Fact]
    public void UnavailableDefaultImporterSetProducesACompilationError()
    {
        Assert.Contains(Compile(ExtensionSource + """

            [AssetCapabilities("96c30d2f-6b98-4a48-a9f1-d2451d48b917")] public static partial class ProjectAssets;
            """).Diagnostics, value => value.Id == "SEAC003"
            && value.GetMessage().Contains("96c30d2f-6b98-4a48-a9f1-d2451d48b917", StringComparison.Ordinal));
    }

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
        bool generators = true, bool capabilities = true, string? assemblyName = null, ImmutableArray<byte> publicKey = default, params MetadataReference[] dependencies)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(Asset).Assembly.Location, typeof(BinarySerializableAttribute).Assembly.Location,
                typeof(IServiceCollection).Assembly.Location, typeof(ServiceCollectionContainerBuilderExtensions).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().Concat(dependencies);
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(assemblyName ?? "AssetExtension" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithCryptoPublicKey(publicKey).WithPublicSign(!publicKey.IsDefaultOrEmpty).WithAllowUnsafe(true));
        if (!generators) return (compilation, []);
        var selected = new List<ISourceGenerator> { new BinaryContractGenerator().AsSourceGenerator(), new AssetBlockGenerator().AsSourceGenerator() };
        if (capabilities) selected.Add(new AssetCapabilitiesGenerator().AsSourceGenerator());
        GeneratorDriver driver = CSharpGeneratorDriver.Create(selected, parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        Assert.All(driver.GetRunResult().Results, result =>
            Assert.True(result.Exception is null, result.Exception?.ToString()));
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
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        Assert.All(driver.GetRunResult().Results, result =>
            Assert.True(result.Exception is null, result.Exception?.ToString()));
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

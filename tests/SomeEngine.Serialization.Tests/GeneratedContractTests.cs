using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class GeneratedContractTests
{
    [Fact]
    public void GeneratedExactContractRoundTripsNestedCollectionsAndNullablePayload()
    {
        var expected = new GeneratedExactContract
        {
            Enabled = true,
            Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
            Name = "生成合同",
            Payload = new byte[] { 1, 2, 3, 5, 8, 13 },
            Points =
            [
                new GeneratedPoint { X = 1.25f, Y = -2.5f },
                new GeneratedPoint { X = 3.75f, Y = 4.5f },
            ],
        };

        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(expected);
        GeneratedExactContract actual = BinaryContractSerializer.Deserialize<GeneratedExactContract>(bytes.Span);

        Assert.True(actual.Enabled);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Payload.Value.ToArray(), actual.Payload!.Value.ToArray());
        Assert.Equal(expected.Points, actual.Points);
        Assert.Equal(BinaryCompatibility.ExactSchema, GeneratedExactContract.Compatibility);
        Assert.NotEqual(Guid.Empty, GeneratedExactContract.TypeId);
        Assert.NotEqual(0UL, GeneratedExactContract.SchemaFingerprint);
    }

    [Fact]
    public void GeneratedNullableEnumUsesPresenceBitAndRoundTripsBothStates()
    {
        OwnedTestEncoding presentBytes = OwnedTestEncoding.Encode(
            new GeneratedNullableEnumContract { Value = GeneratedOptionalValue.Second });
        OwnedTestEncoding absentBytes = OwnedTestEncoding.Encode(
            new GeneratedNullableEnumContract { Value = null });

        GeneratedNullableEnumContract present =
            BinaryContractSerializer.Deserialize<GeneratedNullableEnumContract>(presentBytes.Span);
        GeneratedNullableEnumContract absent =
            BinaryContractSerializer.Deserialize<GeneratedNullableEnumContract>(absentBytes.Span);
        var presentView = new GeneratedNullableEnumContract.SpanView(presentBytes.Span);
        var absentView = new GeneratedNullableEnumContract.SpanView(absentBytes.Span);

        Assert.Equal(GeneratedOptionalValue.Second, present.Value);
        Assert.Null(absent.Value);
        Assert.Equal(GeneratedOptionalValue.Second, presentView.GetValue());
        Assert.Null(absentView.GetValue());
        Assert.False(presentBytes.Span.SequenceEqual(absentBytes.Span));
    }

    [Fact]
    public void FixedDestinationEncodesLargeContractExactlyOnce()
    {
        var expected = new GeneratedExactContract
        {
            Enabled = true,
            Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
            Name = new string('\u754c', 10_000),
            Payload = Enumerable.Range(0, 50_000).Select(static value => (byte)value).ToArray(),
            Points = [new GeneratedPoint { X = 1.25f, Y = -2.5f }],
        };

        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(expected);
        GeneratedExactContract actual = BinaryContractSerializer.Deserialize<GeneratedExactContract>(encoded.Span);

        Assert.True(encoded.Length > 60_000);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Payload.Value.Span, actual.Payload!.Value.Span);
    }

    [Fact]
    public void GeneratedCollectionWriterSnapshotsCountOncePerSerialization()
    {
        var points = new CountProbeList<GeneratedPoint>(
        [
            new GeneratedPoint { X = 1, Y = 2 },
            new GeneratedPoint { X = 3, Y = 4 },
        ]);
        var expected = new GeneratedExactContract { Points = points };

        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(expected);
        GeneratedExactContract actual = BinaryContractSerializer.Deserialize<GeneratedExactContract>(bytes.Span);

        Assert.Equal(1, points.CountReads);
        Assert.Equal(2, actual.Points!.Count);
    }

    [Fact]
    public void GeneratedChunkReferenceIsConcreteOnContractSpanViewAndOwnedView()
    {
        const ulong key = 0xD67A_5531_2400_0001UL;
        var value = new GeneratedChunkContract
        {
            ChunkKey = key,
            DecodedLength = 4096,
            Payload = new byte[4096],
        };
        var expected = new BinaryChunkRef(key, 4096);

        Assert.Equal(expected, value.PayloadChunk);
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(value);
        var spanView = new GeneratedChunkContract.SpanView(encoded.Span);
        Assert.Equal(expected, spanView.PayloadChunk);

        using BinaryContractViewOwner owner = BinaryContractViewOwner.Borrow(encoded.Memory);
        GeneratedChunkContract.View view = GeneratedChunkContract.CreateView(owner);
        Assert.Equal(expected, view.PayloadChunk);
        GeneratedChunkContract decoded = BinaryContractSerializer.Deserialize<GeneratedChunkContract>(encoded.Span);
        Assert.Equal(expected, decoded.PayloadChunk);
        Assert.Null(decoded.Payload);
    }

    [Fact]
    public async Task RootChunkReferenceRejectsDirectoryLengthDisagreementBeforePublication()
    {
        const ulong key = 0xD67A_5531_2400_0002UL;
        var root = new GeneratedChunkContract
        {
            ChunkKey = key,
            DecodedLength = 4,
        };
        BinaryDocumentWriter writer = BinaryDocumentWriter.Create(root);
        writer.AddChunk(
            key,
            new byte[] { 1, 2, 3 },
            BinaryFieldKey.FromName("SomeEngine.Serialization.Tests.Raw.v1"),
            ChunkCompression.None,
            alignment: 16);
        using MappedTestDocument bytes = writer.BuildMapped();
        await using var source = new MemoryRangeSource(bytes.Memory);
        await using BinaryDocument<GeneratedChunkContract> document =
            await BinaryDocument<GeneratedChunkContract>.OpenAsync(source);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await document.AcquireChunkAsync(document.Root.PayloadChunk));
        Assert.Contains("directory length disagrees", error.Message, StringComparison.Ordinal);

        bool destinationAllocated = false;
        InvalidDataException readError = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await document.TryReadChunkAsync(
                document.Root.PayloadChunk,
                length =>
                {
                    destinationAllocated = true;
                    return new byte[length];
                }));
        Assert.Contains("directory length disagrees", readError.Message, StringComparison.Ordinal);
        Assert.False(destinationAllocated);

        InvalidDataException rangeError = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await document.OpenChunkRangeSourceAsync(document.Root.PayloadChunk));
        Assert.Contains("directory length disagrees", rangeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCurrentContractRoundTripsAndRegistersInAotCatalog()
    {
        var expected = new GeneratedCurrentContract
        {
            Id = 42,
            Name = "durable",
            Values = [7, 11, 13],
        };

        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(expected);
        GeneratedCurrentContract actual = BinaryContractSerializer.Deserialize<GeneratedCurrentContract>(bytes.Span);
        var catalog = new BinaryContractCatalog();
        global::SomeEngine.GeneratedContracts.Assembly_SomeEngine_Serialization_Tests
            .GeneratedBinaryContractCatalog.RegisterAll(catalog);
        catalog.Freeze();

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(BinaryCompatibility.ExactSchema, GeneratedCurrentContract.Compatibility);
        Assert.True(catalog.TryGet(GeneratedExactContract.TypeId, out BinaryContractDescriptor exact));
        Assert.Equal(typeof(GeneratedExactContract), exact.ContractType);
        Assert.True(catalog.TryGet(GeneratedCurrentContract.TypeId, out BinaryContractDescriptor current));
        Assert.Equal(typeof(GeneratedCurrentContract), current.ContractType);
    }

    [Fact]
    public void GeneratedCollectionAllocationsAreChargedBeforeAllocation()
    {
        var exact = new GeneratedExactContract
        {
            Points = Enumerable.Range(0, 100)
                .Select(static value => new GeneratedPoint { X = value, Y = -value })
                .ToList(),
        };
        OwnedTestEncoding exactBytes = OwnedTestEncoding.Encode(exact);
        OwnedTestEncoding currentBytes = OwnedTestEncoding.Encode(new GeneratedCurrentContract
        {
            Id = 1,
            Name = "budget",
            Values = [1, 2, 3],
        });

        Assert.Throws<InvalidDataException>(() =>
            BinaryContractSerializer.Deserialize<GeneratedExactContract>(
                exactBytes.Span,
                new BinaryReadLimits { MaxAllocationBytes = 800 }));
        Assert.Throws<InvalidDataException>(() =>
            BinaryContractSerializer.Deserialize<GeneratedCurrentContract>(
                currentBytes.Span,
                new BinaryReadLimits { MaxAllocationBytes = 100 }));
    }

    [Fact]
    public void GeneratedPaddingFreeNativeLayoutProofRoundTripsRecursiveStructs()
    {
        GeneratedNativeVertex[] expected =
        [
            new GeneratedNativeVertex
            {
                Position = new GeneratedNativeVector2 { X = 1.25f, Y = -2.5f },
                Color = 0xFF3366CC,
                Flags = 7,
            },
            new GeneratedNativeVertex
            {
                Position = new GeneratedNativeVector2 { X = 3.75f, Y = 4.5f },
                Color = 0xFF102030,
                Flags = 11,
            },
        ];
        NativeLayoutProof<GeneratedNativeVertex> proof = GeneratedNativeVertex.NativeLayoutProof;
        OwnedTestEncoding encoded = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(encoded.Memory.Span);

        NativeBlock.Write(ref writer, expected, proof);
        encoded.SetLength(writer.WrittenCount);
        var reader = new BinaryDataReader(encoded.Span);
        ReadOnlySpan<GeneratedNativeVertex> actual = NativeBlock.Read<GeneratedNativeVertex>(ref reader, proof);

        Assert.True(NativeBlock.IsSupported(proof));
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < actual.Length; index++)
        {
            Assert.Equal(expected[index].Position.X, actual[index].Position.X);
            Assert.Equal(expected[index].Position.Y, actual[index].Position.Y);
            Assert.Equal(expected[index].Color, actual[index].Color);
            Assert.Equal(expected[index].Flags, actual[index].Flags);
        }
        reader.EnsureFullyConsumed("generated native layout proof");
    }

    [Fact]
    public void GeneratedArraysListsCanonicalDictionariesUnionsAndRecordsRoundTrip()
    {
        var first = new GeneratedShapeRecord
        {
            RequiredName = "record",
            OptionalName = null,
            Numbers = [3, 1, 4],
            Labels = ["alpha", null, "omega"],
            Scores = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["zulu"] = 26,
                ["alpha"] = 1,
                ["middle"] = 13,
            },
            Nodes = new Dictionary<GeneratedNodeKey, IGeneratedNode>
            {
                [GeneratedNodeKey.Second] = new GeneratedTextNode { Text = "two" },
                [GeneratedNodeKey.First] = new GeneratedNumberNode { Number = 1 },
            },
            RequiredNode = new GeneratedNumberNode { Number = 42 },
            OptionalNode = null,
        };
        var second = first with
        {
            Scores = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["middle"] = 13,
                ["zulu"] = 26,
                ["alpha"] = 1,
            },
            Nodes = new Dictionary<GeneratedNodeKey, IGeneratedNode>
            {
                [GeneratedNodeKey.First] = new GeneratedNumberNode { Number = 1 },
                [GeneratedNodeKey.Second] = new GeneratedTextNode { Text = "two" },
            },
        };

        OwnedTestEncoding firstBytes = OwnedTestEncoding.Encode(first);
        OwnedTestEncoding secondBytes = OwnedTestEncoding.Encode(second);
        GeneratedShapeRecord actual = BinaryContractSerializer.Deserialize<GeneratedShapeRecord>(firstBytes.Span);

        Assert.True(firstBytes.Span.SequenceEqual(secondBytes.Span));
        Assert.Equal(first.Numbers, actual.Numbers);
        Assert.Equal(first.Labels, actual.Labels);
        Assert.Equal(first.Scores.OrderBy(static pair => pair.Key), actual.Scores.OrderBy(static pair => pair.Key));
        Assert.IsType<GeneratedNumberNode>(actual.RequiredNode);
        Assert.Null(actual.OptionalNode);
        Assert.IsType<GeneratedNumberNode>(actual.Nodes[GeneratedNodeKey.First]);
        Assert.IsType<GeneratedTextNode>(actual.Nodes[GeneratedNodeKey.Second]);
    }

    [Fact]
    public void GeneratedReaderRejectsNullForNonNullableReferenceMember()
    {
        OwnedTestEncoding input = OwnedTestEncoding.Allocate(32);
        var writer = new BinaryDataWriter(input.Memory.Span);
        writer.WriteString(null);
        input.SetLength(writer.WrittenCount);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadNonNullableString(input));

        Assert.Contains("non-nullable string", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedDictionaryReaderRejectsNonCanonicalKeyOrderBeforePublication()
    {
        OwnedTestEncoding input = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(input.Memory.Span);
        writer.WriteBoolean(true);
        writer.WriteInt32(2);
        writer.WriteString("zulu");
        writer.WriteInt32(1);
        writer.WriteString("alpha");
        writer.WriteInt32(2);
        input.SetLength(writer.WrittenCount);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadDictionary(input));

        Assert.Contains("canonical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedUnionReaderRejectsUnknownTag()
    {
        OwnedTestEncoding input = OwnedTestEncoding.Allocate(32);
        var writer = new BinaryDataWriter(input.Memory.Span);
        writer.WriteBoolean(true);
        writer.WriteUInt32(uint.MaxValue);
        input.SetLength(writer.WrittenCount);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadUnion(input));

        Assert.Contains("Unknown closed binary union tag", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedReadersRejectNullSentinelsForEveryNonNullableReferenceShape()
    {
        byte[] nullSentinel = [0];

        Assert.Contains("non-nullable collection", Assert.Throws<InvalidDataException>(() => ReadList(nullSentinel)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-nullable dictionary", Assert.Throws<InvalidDataException>(() => ReadDictionary(nullSentinel)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-nullable nested contract", Assert.Throws<InvalidDataException>(() => ReadNestedClass(nullSentinel)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-nullable union", Assert.Throws<InvalidDataException>(() => ReadUnion(nullSentinel)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedDictionaryChargesAllocationBeforeCreatingBuckets()
    {
        OwnedTestEncoding input = OwnedTestEncoding.Allocate(32);
        var writer = new BinaryDataWriter(input.Memory.Span);
        writer.WriteBoolean(true);
        writer.WriteInt32(1_000_000);
        input.SetLength(writer.WrittenCount);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadDictionaryWithLimits(
            input,
            new BinaryReadLimits { MaxAllocationBytes = 1_024 }));

        Assert.Contains("allocation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedCurrentReaderRejectsMissingRequiredReferenceField()
    {
        byte[] input = [0xFF, 0xFF, 0xFF, 0xFF];

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadCurrentRequired(input));

        Assert.Contains("non-nullable string", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedSpanViewExposesPrimitiveUtf8AndBlobWithoutMaterializing()
    {
        GeneratedExactContract expected = CreateViewFixture();
        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(expected);
        var view = new GeneratedExactContract.SpanView(bytes.Span);

        view.Validate();

        Assert.True(view.GetEnabled());
        Assert.Equal(expected.Id, view.GetId());
        Assert.True(view.TryGetNameUtf8(out ReadOnlySpan<byte> name));
        Assert.Equal(expected.Name, Encoding.UTF8.GetString(name));
        Assert.True(view.TryGetPayloadBytes(out ReadOnlySpan<byte> payload));
        Assert.True(payload.SequenceEqual(expected.Payload!.Value.Span));
        Assert.False(view.GetPointsEncoded().IsEmpty);
    }

    [Fact]
    public void GeneratedPrimitiveAndSpanViewGettersAllocateZeroBytesAfterWarmup()
    {
        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(CreateViewFixture());
        var view = new GeneratedExactContract.SpanView(bytes.Span);
        _ = ObserveGeneratedView(view, 128);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int observed = ObserveGeneratedView(view, 1_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(observed > 0);
        Assert.Equal(0, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ObserveGeneratedView(
        GeneratedExactContract.SpanView view,
        int iterations)
    {
        int observed = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            observed += view.GetEnabled() ? 1 : 0;
            observed += view.GetId() == Guid.Empty ? 0 : 1;
            observed += view.TryGetNameUtf8(out ReadOnlySpan<byte> name) ? name.Length : 0;
            observed += view.TryGetPayloadBytes(out ReadOnlySpan<byte> payload) ? payload.Length : 0;
        }
        return observed;
    }

    [Fact]
    public void GeneratedLongViewFailsClosedAfterOwnerDispose()
    {
        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(CreateViewFixture());
        using var owner = BinaryContractViewOwner.Borrow(bytes.Memory);
        GeneratedExactContract.View view = GeneratedExactContract.CreateView(owner);

        Assert.True(view.GetEnabled());
        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => view.GetEnabled());
    }

    [Fact]
    public void GeneratedValidationRejectsTruncationCorruptBooleanAndComplexShapeCorruption()
    {
        OwnedTestEncoding truncated = OwnedTestEncoding.Encode(CreateViewFixture());
        OwnedTestEncoding corruptBoolean = OwnedTestEncoding.Encode(CreateViewFixture());
        corruptBoolean[0] = 2;
        OwnedTestEncoding complex = OwnedTestEncoding.Encode(new GeneratedShapeRecord
        {
            RequiredName = "view",
            Numbers = [1, 2, 3],
            Scores = new Dictionary<string, int> { ["a"] = 1 },
            Nodes = new Dictionary<GeneratedNodeKey, IGeneratedNode>
            {
                [GeneratedNodeKey.First] = new GeneratedNumberNode { Number = 7 },
            },
            RequiredNode = new GeneratedTextNode { Text = "nested" },
        });
        complex.SetLength(complex.Length - 1);

        Assert.Throws<InvalidDataException>(() => ValidateGeneratedExact(truncated.Span[..^1]));
        Assert.Throws<InvalidDataException>(() => ValidateGeneratedExact(corruptBoolean.Span));
        Assert.Throws<InvalidDataException>(() => ValidateGeneratedShape(complex.Span));
    }

    [Fact]
    public void GeneratedViewsExposeTheSameBorrowedFieldsWithoutMaterializing()
    {
        GeneratedExactContract expected = CreateViewFixture();
        OwnedTestEncoding bytes = OwnedTestEncoding.Encode(expected);
        var spanView = new GeneratedExactContract.SpanView(bytes.Span);
        using var owner = BinaryContractViewOwner.Borrow(bytes.Memory);
        GeneratedExactContract.View longView = GeneratedExactContract.CreateView(owner);

        Assert.Equal(spanView.GetEnabled(), longView.GetEnabled());
        Assert.Equal(spanView.GetId(), longView.GetId());
        Assert.True(spanView.TryGetNameUtf8(out ReadOnlySpan<byte> spanName));
        Assert.True(longView.TryGetNameUtf8(out ReadOnlySpan<byte> longName));
        Assert.True(spanName.SequenceEqual(longName));
        Assert.True(spanView.TryGetPayloadBytes(out ReadOnlySpan<byte> spanPayload));
        Assert.True(longView.TryGetPayloadBytes(out ReadOnlySpan<byte> longPayload));
        Assert.True(spanPayload.SequenceEqual(longPayload));
    }

    [Fact]
    public async Task GeneratedBinaryDocumentViewRetainsRootLeaseAndInvalidatesCopiedViewOnDispose()
    {
        GeneratedExactContract expected = CreateViewFixture();
        using MappedTestDocument documentBytes = BinaryDocumentWriter.Create(expected).BuildMapped();
        var source = new CountingRangeSource(documentBytes);
        BinaryDocumentView<GeneratedExactContract, GeneratedExactContract.View> document =
            await GeneratedExactContract.OpenDocumentViewAsync(source, ownsSource: true);
        GeneratedExactContract.View copiedRoot = document.Root;

        Assert.True(copiedRoot.GetEnabled());
        Assert.Equal(expected.Id, copiedRoot.GetId());
        Assert.Equal(3, source.Operations.Length);

        await document.DisposeAsync();

        Assert.True(source.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => copiedRoot.GetEnabled());
    }

    private static void ReadNonNullableString(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedNonNullableString.Read(ref reader);
    }

    private static void ReadNonNullableString(OwnedTestEncoding input)
    {
        var reader = new BinaryDataReader(input.Span);
        _ = GeneratedNonNullableString.Read(ref reader);
    }

    private static void ReadDictionary(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedDictionaryOnly.Read(ref reader);
    }

    private static void ReadDictionary(OwnedTestEncoding input)
    {
        var reader = new BinaryDataReader(input.Span);
        _ = GeneratedDictionaryOnly.Read(ref reader);
    }

    private static void ReadDictionaryWithLimits(OwnedTestEncoding input, BinaryReadLimits limits)
    {
        var reader = new BinaryDataReader(input.Span, limits);
        _ = GeneratedDictionaryOnly.Read(ref reader);
    }

    private static void ReadList(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedListOnly.Read(ref reader);
    }

    private static void ReadNestedClass(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedNestedClassOnly.Read(ref reader);
    }

    private static void ReadUnion(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedUnionOnly.Read(ref reader);
    }

    private static void ReadUnion(OwnedTestEncoding input)
    {
        var reader = new BinaryDataReader(input.Span);
        _ = GeneratedUnionOnly.Read(ref reader);
    }

    private static void ReadCurrentRequired(byte[] input)
    {
        var reader = new BinaryDataReader(input);
        _ = GeneratedCurrentRequired.Read(ref reader);
    }

    private static GeneratedExactContract CreateViewFixture() => new()
    {
        Enabled = true,
        Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
        Name = "lazy-视图",
        Payload = new byte[] { 2, 3, 5, 7, 11 },
        Points =
        [
            new GeneratedPoint { X = 1.25f, Y = -2.5f },
            new GeneratedPoint { X = 3.75f, Y = 4.5f },
        ],
    };

    private static void ValidateGeneratedExact(ReadOnlySpan<byte> input)
        => GeneratedExactContract.ValidateCanonical(input);

    private static void ValidateGeneratedShape(ReadOnlySpan<byte> input)
        => GeneratedShapeRecord.ValidateCanonical(input);
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial struct GeneratedPoint
{
    public float X { get; set; }
    public float Y { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryNativeLayout("SomeEngine.Serialization.Tests.GeneratedNativeVector2.v1")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct GeneratedNativeVector2
{
    public float X;
    public float Y;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryNativeLayout("SomeEngine.Serialization.Tests.GeneratedNativeVertex.v1")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct GeneratedNativeVertex
{
    public GeneratedNativeVector2 Position;
    public uint Color;
    public uint Flags;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedExactContract
{
    public bool Enabled { get; set; }
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Memory<byte>? Payload { get; set; }
    public IList<GeneratedPoint>? Points { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedChunkContract
{
    public ulong ChunkKey { get; set; }
    public ulong DecodedLength { get; set; }

    [BinaryChunk(nameof(ChunkKey), nameof(DecodedLength))]
    [BinaryIgnore]
    public Memory<byte>? Payload { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema, Epoch = 3)]
public partial class GeneratedCurrentContract
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public IList<int>? Values { get; set; }
}

public enum GeneratedNodeKey : short
{
    First = 1,
    Second = 2,
}

public enum GeneratedOptionalValue : byte
{
    First = 1,
    Second = 2,
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedNullableEnumContract
{
    public GeneratedOptionalValue? Value { get; set; }
}

[BinaryUnion(typeof(GeneratedNumberNode), typeof(GeneratedTextNode))]
public interface IGeneratedNode;

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryUnionCase(10)]
public sealed partial class GeneratedNumberNode : IGeneratedNode
{
    public int Number { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryUnionCase(20)]
public sealed partial class GeneratedTextNode : IGeneratedNode
{
    public string Text { get; set; } = string.Empty;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial record class GeneratedShapeRecord
{
    public string RequiredName { get; set; } = string.Empty;
    public string? OptionalName { get; set; }
    public int[] Numbers { get; set; } = [];
    public List<string?>? Labels { get; set; }
    public Dictionary<string, int> Scores { get; set; } = new(StringComparer.Ordinal);
    public IDictionary<GeneratedNodeKey, IGeneratedNode> Nodes { get; set; } =
        new Dictionary<GeneratedNodeKey, IGeneratedNode>();
    public IGeneratedNode RequiredNode { get; set; } = new GeneratedNumberNode();
    public IGeneratedNode? OptionalNode { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedNonNullableString
{
    public string Value { get; set; } = string.Empty;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedDictionaryOnly
{
    public Dictionary<string, int> Value { get; set; } = new(StringComparer.Ordinal);
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedUnionOnly
{
    public IGeneratedNode Value { get; set; } = new GeneratedNumberNode();
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedListOnly
{
    public List<int> Value { get; set; } = [];
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class GeneratedNestedClassOnly
{
    public GeneratedNumberNode Value { get; set; } = new();
}

[BinaryContract(BinaryCompatibility.ExactSchema, Epoch = 1)]
public partial class GeneratedCurrentRequired
{
    public string Value { get; set; } = string.Empty;
}

internal sealed class CountProbeList<T>(IEnumerable<T> items) : IList<T>
{
    private readonly List<T> _items = [.. items];

    public int CountReads { get; private set; }

    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public int Count
    {
        get
        {
            CountReads++;
            return _items.Count;
        }
    }

    public bool IsReadOnly => false;
    public void Add(T item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public int IndexOf(T item) => _items.IndexOf(item);
    public void Insert(int index, T item) => _items.Insert(index, item);
    public bool Remove(T item) => _items.Remove(item);
    public void RemoveAt(int index) => _items.RemoveAt(index);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

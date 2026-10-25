using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using Xunit.Abstractions;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryTypeCatalogTests
{
    private readonly ITestOutputHelper _output;

    public BinaryTypeCatalogTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(256)]
    public async Task OpenReturnsRootThenEveryRegisteredType(int additionalCount)
    {
        BinaryWireTypeDescriptor[] types = Enumerable.Range(1, additionalCount)
            .Select(Descriptor).ToArray();
        byte[] bytes = await WriteDocumentAsync(types.Reverse());
        await using var source = new MemoryRangeSource(bytes);
        BinaryEnvelopeMetadata metadata = await BinaryDocument<TestRoot>.InspectAsync(source);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(source);

        var rootType = new BinaryWireTypeDescriptor(TestRoot.TypeId, TestRoot.SchemaFingerprint,
            TestRoot.Compatibility, TestRoot.SchemaEpoch);
        Assert.Equal(types.Prepend(rootType), document.TypeCatalog);
        Assert.Equal(rootType, document.RootType);
        Assert.Equal(rootType.TypeId, metadata.TypeId);
        Assert.Equal(rootType.SchemaFingerprint, metadata.SchemaFingerprint);
        Assert.Equal(rootType.Compatibility, metadata.Compatibility);
        Assert.Equal(rootType.SchemaEpoch, metadata.SchemaEpoch);
        Assert.Equal(document.Generation, metadata.Generation);
        TestRoots.AssertEquivalent(TestRoots.Canonical(text: "type catalog", number: 257), document.Root);
    }

    [Fact]
    public async Task RegistrationOrderDoesNotChangeDocumentBytesOrGeneration()
    {
        BinaryWireTypeDescriptor[] types = Enumerable.Range(1, 256).Select(Descriptor).ToArray();
        byte[] first = await WriteDocumentAsync(types);
        byte[] second = await WriteDocumentAsync(types.Reverse());
        Assert.Equal(first, second);
        await using BinaryDocument<TestRoot> firstDocument = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(first), ownsSource: true);
        await using BinaryDocument<TestRoot> secondDocument = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(second), ownsSource: true);
        Assert.Equal(firstDocument.Generation, secondDocument.Generation);
        Assert.Equal(firstDocument.TypeCatalog, secondDocument.TypeCatalog);
        _output.WriteLine($"A06G_PUBLIC_CATALOG SHA256={Convert.ToHexString(SHA256.HashData(first))} " +
            $"GENERATION={firstDocument.Generation:D} TYPES={firstDocument.TypeCatalog.Count}");
    }

    [Fact]
    public async Task RepeatedIdenticalRegistrationsProduceTheSameCatalog()
    {
        BinaryWireTypeDescriptor[] types = Enumerable.Range(1, 256).Select(Descriptor).ToArray();
        var rootType = new BinaryWireTypeDescriptor(TestRoot.TypeId, TestRoot.SchemaFingerprint,
            TestRoot.Compatibility, TestRoot.SchemaEpoch);
        byte[] once = await WriteDocumentAsync(types);
        byte[] repeated = await WriteDocumentAsync(types.Concat(types.Reverse()).Append(rootType));
        Assert.Equal(once, repeated);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(repeated), ownsSource: true);
        Assert.Equal(types.Prepend(rootType), document.TypeCatalog);
    }

    [Fact]
    public async Task ConflictingRegistrationLeavesTheFirstDescriptorUsable()
    {
        BinaryWireTypeDescriptor type = Descriptor(1);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical(text: "type catalog", number: 257));
        writer.AddContract(type);
        Assert.Throws<InvalidOperationException>(() =>
            writer.AddContract(type with { SchemaFingerprint = type.SchemaFingerprint + 1 }));
        using var destination = new MemoryStream();
        await writer.WriteAsync(destination);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(destination.ToArray()), ownsSource: true);
        Assert.Equal(new[] { writer.RootDescriptor, type }, document.TypeCatalog);
    }

    [Fact]
    public async Task EntryLimitRejectsACatalogAboveTheConfiguredCount()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source,
                limits: new BinaryReadLimits { MaxTypeCatalogEntries = 256 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public async Task EntryLimitAcceptsACatalogAtTheConfiguredCount()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true,
            limits: new BinaryReadLimits { MaxTypeCatalogEntries = 257 });
        Assert.Equal(257, document.TypeCatalog.Count);
        Assert.Equal(257, document.Root.Int32Value);
    }

    [Fact]
    public async Task SmallCatalogByteLimitRejectsTheSameValidDocument()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source,
                limits: new BinaryReadLimits { MaxTypeCatalogBytes = 4 * 1024 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public async Task LargerCatalogByteLimitAcceptsTheSameValidDocument()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true,
            limits: new BinaryReadLimits { MaxTypeCatalogBytes = 16 * 1024 });
        Assert.Equal(257, document.TypeCatalog.Count);
        Assert.Equal(257, document.Root.Int32Value);
    }

    [Fact]
    public async Task SmallerAllocationBudgetRejectsTheSameValidDocument()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source,
                limits: new BinaryReadLimits { MaxAllocationBytes = 16 * 1024 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public async Task LargerAllocationBudgetAcceptsTheSameValidDocument()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true,
            limits: new BinaryReadLimits { MaxAllocationBytes = 64 * 1024 });
        Assert.Equal(257, document.TypeCatalog.Count);
        Assert.Equal(257, document.Root.Int32Value);
    }

    [Fact]
    public async Task RejectedOwnedCatalogOpenClosesTheSuppliedSource()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true,
                limits: new BinaryReadLimits { MaxTypeCatalogEntries = 256 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadExactlyAsync(0, new byte[1]).AsTask());
    }

    [Fact]
    public async Task RejectedBorrowedCatalogOpenLeavesTheSuppliedSourceUsable()
    {
        byte[] bytes = await WriteDocumentAsync(Enumerable.Range(1, 256).Select(Descriptor));
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: false,
                limits: new BinaryReadLimits { MaxTypeCatalogEntries = 256 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
        byte[] first = new byte[1];
        await source.ReadExactlyAsync(0, first);
        Assert.Equal(bytes[0], first[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task UndefinedRegisteredCompatibilityIsRejectedWhenOpened(int invalidNumber)
    {
        BinaryWireTypeDescriptor[] types = Enumerable.Range(1, 256).Select(Descriptor).ToArray();
        types[invalidNumber - 1] = Descriptor(invalidNumber) with
        {
            Compatibility = (BinaryCompatibility)byte.MaxValue,
        };
        byte[] bytes = await WriteDocumentAsync(types.Reverse());
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source).AsTask());
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    private static BinaryWireTypeDescriptor Descriptor(int number)
        => new(Guid.Parse($"{number:X8}-0000-0000-0000-000000000000"),
            0xC0000000UL + checked((ulong)number), BinaryCompatibility.ExactSchema,
            checked((uint)(1 + number % 7)));

    private static async Task<byte[]> WriteDocumentAsync(IEnumerable<BinaryWireTypeDescriptor> types)
    {
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical(text: "type catalog", number: 257));
        foreach (BinaryWireTypeDescriptor type in types)
            writer.AddContract(type);
        using var destination = new MemoryStream();
        await writer.WriteAsync(destination);
        return destination.ToArray();
    }
}

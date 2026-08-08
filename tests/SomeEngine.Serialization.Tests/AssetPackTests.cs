using System.Buffers.Binary;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Serialization.Tests;

public sealed class AssetPackTests
{
    [Fact]
    public async Task PackRoundTripsNestedBinaryDocumentsAndOverlayUsesHighestPriorityEntry()
    {
        Guid sharedAsset = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid baseOnlyAsset = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        using MappedTestDocument baseSharedDocument = BuildAssetDocument("base-shared", 1);
        using MappedTestDocument hotSharedDocument = BuildAssetDocument("hotfix-shared", 2);
        using MappedTestDocument baseOnlyDocument = BuildAssetDocument("base-only", 3);

        using MappedTestDocument baseBytes = new AssetPackBuilder()
            .AddAsset(sharedAsset, "mesh", baseSharedDocument, TestRoot.Fingerprint)
            .AddAsset(baseOnlyAsset, "texture", baseOnlyDocument, TestRoot.Fingerprint)
            .BuildMapped();
        using MappedTestDocument hotfixBytes = new AssetPackBuilder()
            .AddAsset(sharedAsset, "mesh", hotSharedDocument, TestRoot.Fingerprint)
            .BuildMapped();

        AssetPack basePack = await AssetPack.OpenAsync(
            new MemoryRangeSource(baseBytes),
            ownsSource: true);
        AssetPack hotfixPack = await AssetPack.OpenAsync(
            new MemoryRangeSource(hotfixBytes),
            ownsSource: true);
        await using var overlay = new AssetPackOverlay([hotfixPack, basePack]);

        Assert.Equal(2, basePack.Count);
        Assert.Equal(1, hotfixPack.Count);
        Assert.True(overlay.TryResolve(sharedAsset, out AssetPack? resolvedPack, out AssetPackEntry? resolvedEntry));
        Assert.Same(hotfixPack, resolvedPack);
        Assert.Equal("mesh", resolvedEntry!.AssetType);
        Assert.Equal(TestRoot.Fingerprint, resolvedEntry.SchemaFingerprint);

        await using BinaryDocument<TestRoot> sharedDocument =
            await OpenPackDataAsync<TestRoot>(overlay, sharedAsset);
        await using BinaryDocument<TestRoot> baseDocument =
            await OpenPackDataAsync<TestRoot>(overlay, baseOnlyAsset);

        Assert.Equal("hotfix-shared", sharedDocument.Root.Text);
        Assert.Equal(2, sharedDocument.Root.Int32Value);
        Assert.Equal("base-only", baseDocument.Root.Text);
        Assert.Equal(3, baseDocument.Root.Int32Value);
    }

    [Fact]
    public async Task PackOnlyExposesNestedContentThroughAuthenticatedDocumentBoundary()
    {
        Guid assetId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        using MappedTestDocument assetDocument = BuildAssetDocument("range-source", 99);
        using MappedTestDocument packBytes = new AssetPackBuilder()
            .AddAsset(assetId, "scene", assetDocument, TestRoot.Fingerprint)
            .BuildMapped();
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(packBytes),
            ownsSource: true);
        await using BinaryDocument<TestRoot> opened =
            await OpenPackDataAsync<TestRoot>(pack, assetId);
        using ChunkLease chunk = await opened.AcquireChunkAsync(1001);

        Assert.Equal("range-source", opened.Root.Text);
        Assert.True(chunk.Memory.Span.SequenceEqual(new byte[] { 1, 4, 9, 16 }));
    }

    [Fact]
    public async Task PatchBuilderExcludesUnchangedDocumentsAndKeepsOnlyChangedAssets()
    {
        Guid unchangedId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid changedId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        using MappedTestDocument unchanged = BuildAssetDocument("same", 1);
        using MappedTestDocument before = BuildAssetDocument("before", 2);
        using MappedTestDocument after = BuildAssetDocument("after", 3);
        using MappedTestDocument baseBytes = new AssetPackBuilder()
            .AddAsset(unchangedId, "mesh", unchanged, TestRoot.Fingerprint)
            .AddAsset(changedId, "mesh", before, TestRoot.Fingerprint)
            .BuildMapped();

        await using AssetPack basePack = await AssetPack.OpenAsync(
            new MemoryRangeSource(baseBytes),
            ownsSource: true);
        var patchBuilder = new AssetPackPatchBuilder(basePack)
            .AddAsset(unchangedId, "mesh", unchanged, TestRoot.Fingerprint)
            .AddAsset(changedId, "mesh", after, TestRoot.Fingerprint);
        using MappedTestDocument patchBytes = patchBuilder.BuildMapped();

        Assert.Equal([changedId], patchBuilder.ChangedAssetIds);
        await using AssetPack patch = await AssetPack.OpenAsync(
            new MemoryRangeSource(patchBytes),
            ownsSource: true);
        Assert.Equal(1, patch.Count);
        Assert.False(patch.TryGetEntry(unchangedId, out _));
        await using BinaryDocument<TestRoot> changed =
            await OpenPackDataAsync<TestRoot>(patch, changedId);
        Assert.Equal("after", changed.Root.Text);
        Assert.Equal(3, changed.Root.Int32Value);
    }

    [Fact]
    public async Task PatchBuilderRepairsBaseAssetWhoseOuterContentNoLongerMatchesItsDescriptor()
    {
        Guid assetId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        using MappedTestDocument document = BuildAssetDocument("repair-corrupt-base", 17);
        using MappedTestDocument corruptPack = new AssetPackBuilder()
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint)
            .BuildMapped();

        long directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(corruptPack.AsSpan(64, 8));
        long assetOffset = BinaryPrimitives.ReadInt64LittleEndian(
            corruptPack.AsSpan(checked((int)directoryOffset + 16), 8));
        int corruptOffset = checked((int)assetOffset + document.Length - 1);
        corruptPack[corruptOffset] ^= 0x5A;

        await using AssetPack basePack = await AssetPack.OpenAsync(
            new MemoryRangeSource(corruptPack),
            ownsSource: true);
        var patchBuilder = new AssetPackPatchBuilder(basePack)
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint);

        using MappedTestDocument patchBytes = patchBuilder.BuildMapped();

        Assert.Equal([assetId], patchBuilder.ChangedAssetIds);
        await using AssetPack patch = await AssetPack.OpenAsync(
            new MemoryRangeSource(patchBytes),
            ownsSource: true);
        await using BinaryDocument<TestRoot> repaired =
            await OpenPackDataAsync<TestRoot>(patch, assetId);
        Assert.Equal("repair-corrupt-base", repaired.Root.Text);
    }

    [Fact]
    public async Task PatchTreatsAssetTypeAsContentIdentityAndRejectsFalseSchemaMetadata()
    {
        Guid assetId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        using MappedTestDocument document = BuildAssetDocument("metadata", 23);
        using MappedTestDocument baseBytes = new AssetPackBuilder()
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint)
            .BuildMapped();
        await using AssetPack basePack = await AssetPack.OpenAsync(
            new MemoryRangeSource(baseBytes),
            ownsSource: true);

        var typePatch = new AssetPackPatchBuilder(basePack)
            .AddAsset(assetId, "collision-mesh", document, TestRoot.Fingerprint);
        using MappedTestDocument patchBytes = typePatch.BuildMapped();
        Assert.Equal([assetId], typePatch.ChangedAssetIds);
        await using AssetPack patch = await AssetPack.OpenAsync(
            new MemoryRangeSource(patchBytes),
            ownsSource: true);
        Assert.True(patch.TryGetEntry(assetId, out AssetPackEntry? changed));
        Assert.Equal("collision-mesh", changed!.AssetType);

        var invalidFingerprint = new AssetPackPatchBuilder(basePack)
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint ^ 1UL);
        Assert.Throws<ArgumentException>(() => invalidFingerprint.BuildMapped());
    }

    [Fact]
    public async Task PackRejectsExactSchemaFingerprintMismatchWithoutMigration()
    {
        Guid assetId = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var root = new GeneratedCurrentContract
        {
            Id = 17,
            Name = "current-pack",
            Values = [2, 3, 5],
        };
        using MappedTestDocument mismatchedDocument = BinaryDocumentWriter.Create(root).BuildMapped();
        const ulong mismatchedFingerprint = 0x8A77665544332211UL;
        RewriteRootFingerprint(mismatchedDocument.AsSpan(), mismatchedFingerprint);
        using MappedTestDocument packBytes = new AssetPackBuilder()
            .AddAsset(assetId, "save", mismatchedDocument, mismatchedFingerprint)
            .BuildMapped();

        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(packBytes),
            ownsSource: true);
        BinarySchemaMismatchException error = await Assert.ThrowsAsync<BinarySchemaMismatchException>(() =>
            OpenPackDataAsync<GeneratedCurrentContract>(pack, assetId).AsTask());

        Assert.Contains("fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviousPackFooterFailsClosed()
    {
        Guid assetId = Guid.Parse("51000000-0000-0000-0000-000000000005");
        using MappedTestDocument document = BuildAssetDocument("previous-pack", 18);
        using MappedTestDocument packBytes = new AssetPackBuilder()
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint)
            .BuildMapped();
        "SEPACK01"u8.CopyTo(packBytes.AsSpan(packBytes.Length - 32, 8));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AssetPack.OpenAsync(new MemoryRangeSource(packBytes), ownsSource: true).AsTask());

        Assert.Contains("current schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackBuilderAuthenticatesMetadataWithoutPredecodingNestedPayload()
    {
        Guid assetId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        using MappedTestDocument corrupted = BuildAssetDocument("offline-validation", 31);
        int directoryOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(corrupted.AsSpan(64, 8)));
        int chunkOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            corrupted.AsSpan(directoryOffset + 16, 8)));
        corrupted[chunkOffset] ^= 0x7F;

        using MappedTestDocument packed = new AssetPackBuilder()
            .AddAsset(assetId, "mesh", corrupted, TestRoot.Fingerprint)
            .BuildMapped();
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(packed),
            ownsSource: true);
        await using BinaryDocument<TestRoot> document =
            await OpenPackDataAsync<TestRoot>(pack, assetId);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            document.AcquireChunkAsync(1001).AsTask());
        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedPackAuthenticatesOuterReceiptInnerMetadataAndLazyChunkContent()
    {
        Guid assetId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        using MappedTestDocument document = BuildAssetDocument("signed-pack", 37);
        using RSA signingKey = RSA.Create();
        signingKey.KeySize = 2048;
        var builder = new AssetPackBuilder()
            .AddAsset(assetId, "mesh", document, TestRoot.Fingerprint);

        using MappedTestDocument first = builder.BuildSignedMapped(signingKey);
        using MappedTestDocument second = builder.BuildSignedMapped(signingKey);

        Assert.Equal(first, second);
        await using AssetPack verified = await AssetPack.OpenVerifiedAsync(
            new MemoryRangeSource(first),
            signingKey,
            ownsSource: true);
        Assert.True(verified.HasSignature);
        await using BinaryDocument<TestRoot> opened =
            await OpenPackDataAsync<TestRoot>(verified, assetId);
        Assert.Equal("signed-pack", opened.Root.Text);

        using RSA wrongKey = RSA.Create();
        wrongKey.KeySize = 2048;
        await Assert.ThrowsAsync<CryptographicException>(() => AssetPack.OpenVerifiedAsync(
            new MemoryRangeSource(first),
            wrongKey,
            ownsSource: true).AsTask());

        using MappedTestDocument metadataCorrupted = builder.BuildSignedMapped(signingKey);
        int outerDirectoryOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            metadataCorrupted.AsSpan(64, 8)));
        int assetOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            metadataCorrupted.AsSpan(outerDirectoryOffset + 16, 8)));
        RewriteGeneration(metadataCorrupted.AsSpan(assetOffset, document.Length));
        await using AssetPack metadataVerified = await AssetPack.OpenVerifiedAsync(
            new MemoryRangeSource(metadataCorrupted),
            signingKey,
            ownsSource: true);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            OpenPackDataAsync<TestRoot>(metadataVerified, assetId).AsTask());

        using MappedTestDocument payloadCorrupted = builder.BuildSignedMapped(signingKey);
        outerDirectoryOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            payloadCorrupted.AsSpan(64, 8)));
        assetOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            payloadCorrupted.AsSpan(outerDirectoryOffset + 16, 8)));
        int innerDirectoryOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            payloadCorrupted.AsSpan(assetOffset + 64, 8)));
        int chunkOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            payloadCorrupted.AsSpan(assetOffset + innerDirectoryOffset + 16, 8)));
        payloadCorrupted[assetOffset + chunkOffset] ^= 0x5A;
        await using AssetPack payloadVerified = await AssetPack.OpenVerifiedAsync(
            new MemoryRangeSource(payloadCorrupted),
            signingKey,
            ownsSource: true);
        await using BinaryDocument<TestRoot> payloadDocument =
            await OpenPackDataAsync<TestRoot>(payloadVerified, assetId);
        InvalidDataException payloadError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            payloadDocument.AcquireChunkAsync(1001).AsTask());
        Assert.Contains("SHA-256", payloadError.Message, StringComparison.OrdinalIgnoreCase);

        using MappedTestDocument unsigned = builder.BuildMapped();
        await Assert.ThrowsAsync<CryptographicException>(() => AssetPack.OpenVerifiedAsync(
            new MemoryRangeSource(unsigned),
            signingKey,
            ownsSource: true).AsTask());
    }

    private static MappedTestDocument BuildAssetDocument(string text, int number)
        => BinaryDocumentWriter.Create(TestRoots.Canonical(text: text, number: number))
            .AddChunk(1001, [1, 4, 9, 16], typeFingerprint: 0xA55E7UL)
            .BuildMapped();

    private static async ValueTask<BinaryDocument<T>> OpenPackDataAsync<T>(
        AssetPack pack,
        Guid assetId,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        where T : IBinaryContract<T>
    {
        IRangeSource source = await pack.OpenAssetSourceAsync(assetId, cancellationToken);
        return await BinaryDocument<T>.OpenAsync(
            source,
            ownsSource: true,
            limits,
            cancellationToken);
    }

    private static async ValueTask<BinaryDocument<T>> OpenPackDataAsync<T>(
        AssetPackOverlay overlay,
        Guid assetId,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        where T : IBinaryContract<T>
    {
        IRangeSource source = await overlay.OpenAssetSourceAsync(assetId, cancellationToken);
        return await BinaryDocument<T>.OpenAsync(
            source,
            ownsSource: true,
            limits,
            cancellationToken);
    }

    private static void RewriteRootFingerprint(Span<byte> document, ulong fingerprint)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(document[24..], fingerprint);
        int catalogLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(document[120..]));
        Span<byte> catalog = document.Slice(128, catalogLength);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(catalog));
        BinaryPrimitives.WriteUInt64LittleEndian(catalog[20..], fingerprint);
        int catalogPayloadLength = catalogLength - 32;
        SHA256.HashData(catalog[..catalogPayloadLength], catalog[catalogPayloadLength..]);
        Span<byte> headerHash = stackalloc byte[32];
        SHA256.HashData(document[..124], headerHash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            document[124..],
            BinaryPrimitives.ReadUInt32LittleEndian(headerHash));
    }

    private static void RewriteGeneration(Span<byte> document)
    {
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
            .TryWriteBytes(document[32..48], bigEndian: true, out _);
        Span<byte> headerHash = stackalloc byte[32];
        SHA256.HashData(document[..124], headerHash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            document[124..],
            BinaryPrimitives.ReadUInt32LittleEndian(headerHash));
    }
}

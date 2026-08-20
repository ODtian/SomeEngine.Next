using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpPipelineCacheTests
{
    [Fact]
    public void Pipeline_compatibility_and_standard_module_share_the_pinned_Slang_identity()
    {
        Assert.Equal("2026.14", SlangToolchainIdentity.Version);
        Assert.Equal(
            "slang-standard-module-" + SlangToolchainIdentity.Version,
            SlangToolchainIdentity.StandardModuleDirectory);
    }

    [Fact]
    public async Task Compute_pipeline_cache_can_be_prewarmed_off_thread_and_replayed()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_cache_off_thread_prewarm",
            source,
            [new("computeMain", SlangStage.Compute)]);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(device, default);
        ComputePipelineDesc description = new(
            shader.Program,
            shader.GetEntryPoint(0),
            "off-thread prewarm");

        Pipeline[] warmed = await Task.Factory.StartNew(
            () =>
            {
                var result = new Pipeline[4];
                try
                {
                    for (int index = 0; index < result.Length; index++)
                        result[index] = backend.CreateComputePipeline(device, description, cache);
                    return result;
                }
                catch
                {
                    foreach (Pipeline? pipeline in result)
                        pipeline?.Dispose();
                    throw;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        foreach (Pipeline pipeline in warmed)
            pipeline.Dispose();

        byte[] envelope = ReadCache(backend, cache);
        Assert.NotEqual(Convert.FromHexString(EmptyEnvelopeGolden), envelope);
        using PipelineCache reloaded = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelope));
        using Pipeline replayed = await Task.Factory.StartNew(
            () => backend.CreateComputePipeline(device, description, reloaded),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.Equal(envelope, ReadCache(backend, reloaded));
    }

    private const string EmptyEnvelopeGolden =
        "53455248494330310300000000000000" +
        "B5F55AD2B4F7C32572EA7FD1CE282773992F372066106B146B907ED00A3020ED";

    private const string UnsupportedSchemaEnvelope =
        "53455248494330316300000000000000" +
        "75C7EFD1EC755944C379F7094CCF431F078846061397E7F903A044F27158CDAA";

    private const string UnknownBackendAndFamilyEnvelope =
        "534552484943303103000000010000001122334455667788FE" +
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F" +
        "04000000DEADBEEF" +
        "5F78C33274E43FA9DE5659265C1D917E25C03722DCB0B8D27DB8D5FEAA813953" +
        "9A1C8777367E05FFA06902BE6E296C18A041526EF59BAAEF5546D61C3AC56E7D";

    [Fact]
    public void Hard_entry_corruption_boundary_and_failed_merge_are_atomic()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        const uint hardEntryCount = 1_000_000;

        GraphicsException boundary = Assert.Throws<GraphicsException>(() =>
            backend.CreatePipelineCache(
                device,
                new PipelineCacheDesc(CreateCountOnlyEnvelope(hardEntryCount))));
        Assert.IsType<EndOfStreamException>(boundary.InnerException);
        GraphicsException overflow = Assert.Throws<GraphicsException>(() =>
            backend.CreatePipelineCache(
                device,
                new PipelineCacheDesc(CreateCountOnlyEnvelope(hardEntryCount + 1))));
        Assert.IsType<InvalidDataException>(overflow.InnerException);

        using PipelineCache destination = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(Convert.FromHexString(UnknownBackendAndFamilyEnvelope)));
        PipelineCache disposedSource = backend.CreatePipelineCache(device, default);
        disposedSource.Dispose();
        byte[] before = ReadCache(backend, destination);

        Assert.Throws<ObjectDisposedException>(() =>
            backend.MergePipelineCaches(destination, [disposedSource]));
        Assert.Equal(before, ReadCache(backend, destination));
    }

    [Fact]
    public void Public_cache_policy_rejects_invalid_limits_and_preserves_device_usability()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        int emptyEnvelopeByteCount = Convert.FromHexString(EmptyEnvelopeGolden).Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumEntryCount: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumDecodedByteCount: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: emptyEnvelopeByteCount - 1)));

        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                maximumEntryCount: 1,
                maximumByteCount: emptyEnvelopeByteCount));
        Assert.Equal(Convert.FromHexString(EmptyEnvelopeGolden), ReadCache(backend, cache));
    }

    [Fact]
    public void Pre_cancelled_cache_operations_and_short_output_leave_outputs_unchanged()
    {
        byte[] sectionA = CreateUnknownSection(0x01, [0xA1]);
        byte[] sectionB = CreateUnknownSection(0x02, [0xB2]);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionA)),
            cancellation.Token));

        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionA)));
        byte[] output = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        int cancelledRequired = -1;
        Assert.Throws<OperationCanceledException>(() => backend.TryGetPipelineCacheData(
            cache,
            output,
            out cancelledRequired,
            cancellation.Token));
        Assert.All(output, value => Assert.Equal((byte)0xCD, value));

        byte[] expected = ReadCache(backend, cache);
        byte[] shortOutput = Enumerable.Repeat((byte)0xA5, expected.Length - 1).ToArray();
        Assert.False(backend.TryGetPipelineCacheData(
            cache,
            shortOutput,
            out int requiredByteCount));
        Assert.Equal(expected.Length, requiredByteCount);
        Assert.All(shortOutput, value => Assert.Equal((byte)0xA5, value));

        using PipelineCache destination = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionA)));
        using PipelineCache source = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionB)));
        byte[] beforeMerge = ReadCache(backend, destination);
        Assert.Throws<OperationCanceledException>(() => backend.MergePipelineCaches(
            destination,
            [source],
            cancellation.Token));
        Assert.Equal(beforeMerge, ReadCache(backend, destination));
    }

    [Fact]
    public void Validation_merge_polls_cancellation_after_entry_check_before_scanning_sources()
    {
        byte[] sectionA = CreateUnknownSection(0x01, [0xA1]);
        byte[] sectionB = CreateUnknownSection(0x02, [0xB2]);
        using var backend = new ValidationLayer(new D3D12Backend());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache destination = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionA)));
        using PipelineCache source = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionB)));
        byte[] before = ReadCache(backend, destination);
        source.Dispose();
        object validationGate = D3D12PrivateState.GetField(backend, "_gate")
            .GetValue(backend)!;
        using var entered = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            entered.Set();
            try
            {
                backend.MergePipelineCaches(destination, [source], cancellation.Token);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        Monitor.Enter(validationGate);
        try
        {
            worker.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
        }
        finally
        {
            Monitor.Exit(validationGate);
        }

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<OperationCanceledException>(failure);
        Assert.Equal(before, ReadCache(backend, destination));
    }

    [Fact]
    public void Validation_layer_reports_negative_cache_policy_and_allows_a_valid_retry()
    {
        var messages = new List<ValidationMessage>();
        using var backend = new ValidationLayer(
            new D3D12Backend(),
            new ValidationOptions(new DelegateValidationMessageSink(messages.Add)));
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumEntryCount: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: -1)));

        Assert.Equal(
            2,
            messages.Count(static message =>
                message.Type == ValidationMessageType.Error &&
                message.Area == "PipelineCache"));
        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumEntryCount: 1));
        Assert.Equal(Convert.FromHexString(EmptyEnvelopeGolden), ReadCache(backend, cache));
    }

    [Fact]
    public void Import_limits_accept_exact_boundaries_and_reject_the_whole_oversized_envelope()
    {
        byte[] sectionA = CreateUnknownSection(0x01, [0xA1, 0xA2]);
        byte[] sectionB = CreateUnknownSection(0x02, [0xB1, 0xB2, 0xB3]);
        byte[] envelope = CreateEnvelope(sectionA, sectionB);
        const int decodedByteCount = 5;
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        using PipelineCache exact = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                envelope,
                maximumEntryCount: 2,
                maximumByteCount: envelope.Length,
                maximumDecodedByteCount: decodedByteCount));
        Assert.Equal(envelope, ReadCache(backend, exact));

        Assert.Throws<ArgumentException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelope, maximumEntryCount: 1)));
        Assert.Throws<ArgumentException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelope, maximumByteCount: envelope.Length - 1)));
        Assert.Throws<ArgumentException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                envelope,
                maximumDecodedByteCount: decodedByteCount - 1)));

        using PipelineCache retry = backend.CreatePipelineCache(device, default);
        Assert.Equal(Convert.FromHexString(EmptyEnvelopeGolden), ReadCache(backend, retry));
    }

    [Fact]
    public void Import_export_checksums_match_bcl_sha256_at_padding_and_chunk_boundaries()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        int[] payloadLengths = [0, 1, 55, 56, 63, 64, 65, 65_535, 65_536, 65_537];
        foreach (int payloadLength in payloadLengths)
        {
            byte[] payload = CreatePayload(payloadLength);
            byte[] section = CreateUnknownSection(0x7F, payload);
            byte[] envelope = CreateEnvelope(section);
            Assert.Equal(
                SHA256.HashData(payload),
                section.AsSpan(section.Length - 32, 32).ToArray());
            Assert.Equal(
                SHA256.HashData(envelope.AsSpan(0, envelope.Length - 32)),
                envelope.AsSpan(envelope.Length - 32, 32).ToArray());
            using PipelineCache cache = backend.CreatePipelineCache(
                device,
                new PipelineCacheDesc(envelope));
            Assert.Equal(envelope, ReadCache(backend, cache));
        }

        int[] envelopeBodyLengths =
        [
            128,
            129,
            183,
            184,
            191,
            192,
            193,
            65_535,
            65_536,
            65_537,
        ];
        foreach (int bodyLength in envelopeBodyLengths)
        {
            const int singleSectionBodyOverhead = 125;
            byte[] payload = CreatePayload(bodyLength - singleSectionBodyOverhead);
            byte[] envelope = CreateEnvelope(CreateUnknownSection(0x7E, payload));
            Assert.Equal(bodyLength, envelope.Length - 32);
            Assert.Equal(
                SHA256.HashData(envelope.AsSpan(0, bodyLength)),
                envelope.AsSpan(bodyLength, 32).ToArray());
            using PipelineCache cache = backend.CreatePipelineCache(
                device,
                new PipelineCacheDesc(envelope));
            Assert.Equal(envelope, ReadCache(backend, cache));
        }
    }

    [Fact]
    public void Merge_union_over_any_destination_limit_is_atomic_and_preserves_existing_sections()
    {
        byte[] sectionA = CreateUnknownSection(0x01, [0xA1]);
        byte[] sectionB = CreateUnknownSection(0x02, [0xB1, 0xB2]);
        byte[] envelopeA = CreateEnvelope(sectionA);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache source = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionB)));
        using PipelineCache entryLimited = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelopeA, maximumEntryCount: 1));
        using PipelineCache byteLimited = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelopeA, maximumByteCount: envelopeA.Length));
        using PipelineCache decodedLimited = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelopeA, maximumDecodedByteCount: 1));

        foreach (PipelineCache destination in new[]
        {
            entryLimited,
            byteLimited,
            decodedLimited,
        })
        {
            byte[] before = ReadCache(backend, destination);
            Assert.Throws<ArgumentException>(() => backend.MergePipelineCaches(
                destination,
                [source]));
            Assert.Equal(before, ReadCache(backend, destination));
        }
    }

    [Fact]
    public void Merge_limit_rejection_does_not_clone_a_large_payload_or_materialize_an_oversized_union()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        byte[] destinationSection = CreateUnknownSectionWithKey(0, [0xD0]);
        byte[] destinationEnvelope = CreateEnvelope(destinationSection);
        byte[] largePayload = CreatePayload(4 * 1024 * 1024);
        using PipelineCache largeSource = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(
                CreateUnknownSectionWithKey(1, largePayload))));
        using PipelineCache decodedLimited = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                destinationEnvelope,
                maximumDecodedByteCount: 1));
        byte[] decodedBefore = ReadCache(backend, decodedLimited);
        Action rejectLargePayload = () => backend.MergePipelineCaches(
            decodedLimited,
            [largeSource]);
        Assert.Throws<ArgumentException>(rejectLargePayload);
        long beforeLargeRejection = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(rejectLargePayload);
        long largeRejectionBytes =
            GC.GetAllocatedBytesForCurrentThread() - beforeLargeRejection;
        Assert.True(
            largeRejectionBytes < largePayload.Length / 16,
            $"Merge allocated {largeRejectionBytes} bytes for a rejected " +
            $"{largePayload.Length}-byte source payload.");
        Assert.Equal(decodedBefore, ReadCache(backend, decodedLimited));

        const int sourceEntryCount = 4_096;
        var smallSections = new byte[sourceEntryCount][];
        for (uint index = 0; index < sourceEntryCount; index++)
            smallSections[index] = CreateUnknownSectionWithKey(index + 1, [(byte)index]);
        using PipelineCache manyEntrySource = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(smallSections)));
        using PipelineCache entryLimited = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(destinationEnvelope, maximumEntryCount: 1));
        byte[] entryBefore = ReadCache(backend, entryLimited);
        Action rejectEntryUnion = () => backend.MergePipelineCaches(
            entryLimited,
            [manyEntrySource]);
        Assert.Throws<ArgumentException>(rejectEntryUnion);
        long beforeEntryRejection = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(rejectEntryUnion);
        long entryRejectionBytes =
            GC.GetAllocatedBytesForCurrentThread() - beforeEntryRejection;
        Assert.True(
            entryRejectionBytes < 64 * 1024,
            $"Merge allocated {entryRejectionBytes} bytes before rejecting an entry-count union.");
        Assert.Equal(entryBefore, ReadCache(backend, entryLimited));
    }

    [Fact]
    public void Opposing_concurrent_merges_complete_with_one_lock_order()
    {
        byte[] sectionA = CreateUnknownSectionWithKey(1, [0xA1]);
        byte[] sectionB = CreateUnknownSectionWithKey(2, [0xB2]);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cacheA = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionA)));
        using PipelineCache cacheB = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(sectionB)));
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        Exception? failureA = null;
        Exception? failureB = null;
        var mergeIntoA = new Thread(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                backend.MergePipelineCaches(cacheA, [cacheB]);
            }
            catch (Exception exception)
            {
                failureA = exception;
            }
        });
        var mergeIntoB = new Thread(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                backend.MergePipelineCaches(cacheB, [cacheA]);
            }
            catch (Exception exception)
            {
                failureB = exception;
            }
        });

        mergeIntoA.Start();
        mergeIntoB.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        Assert.True(mergeIntoA.Join(TimeSpan.FromSeconds(10)));
        Assert.True(mergeIntoB.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failureA);
        Assert.Null(failureB);
        byte[] expected = CreateEnvelope(sectionA, sectionB);
        Assert.Equal(expected, ReadCache(backend, cacheA));
        Assert.Equal(expected, ReadCache(backend, cacheB));
    }

    [Fact]
    public void Merge_chooses_the_canonical_smaller_payload_for_the_same_key()
    {
        byte[] largerPayloadSection = CreateUnknownSection(0x05, [0xF0]);
        byte[] smallerPayloadSection = CreateUnknownSection(0x05, [0x10]);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache largerPayload = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(largerPayloadSection)));
        using PipelineCache smallerPayload = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(smallerPayloadSection)));
        using PipelineCache firstOrder = backend.CreatePipelineCache(device, default);
        using PipelineCache secondOrder = backend.CreatePipelineCache(device, default);

        backend.MergePipelineCaches(firstOrder, [largerPayload, smallerPayload]);
        backend.MergePipelineCaches(secondOrder, [smallerPayload, largerPayload]);

        byte[] expected = CreateEnvelope(smallerPayloadSection);
        Assert.Equal(expected, ReadCache(backend, firstOrder));
        Assert.Equal(expected, ReadCache(backend, secondOrder));
    }

    [Fact]
    public void Merge_canonical_winner_is_source_order_independent_when_an_intermediate_winner_exceeds_policy()
    {
        byte[] oversizedPayload = CreatePayload(128 * 1024);
        oversizedPayload[0] = 0x10;
        byte[] oversizedSection = CreateUnknownSectionWithKey(0x31, oversizedPayload);
        byte[] finalSection = CreateUnknownSectionWithKey(0x31, [0x00]);
        byte[] oversizedEnvelope = CreateEnvelope(oversizedSection);
        byte[] finalEnvelope = CreateEnvelope(finalSection);
        Assert.True(oversizedPayload.Length > 1);
        Assert.True(oversizedEnvelope.Length > finalEnvelope.Length);
        var destinationPolicy = new PipelineCacheDesc(
            maximumEntryCount: 1,
            maximumByteCount: finalEnvelope.Length,
            maximumDecodedByteCount: 1);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache oversized = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(oversizedEnvelope));
        using PipelineCache final = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(finalEnvelope));
        using PipelineCache firstOrder = backend.CreatePipelineCache(device, destinationPolicy);
        using PipelineCache secondOrder = backend.CreatePipelineCache(device, destinationPolicy);

        backend.MergePipelineCaches(firstOrder, [oversized, final]);
        backend.MergePipelineCaches(secondOrder, [final, oversized]);

        byte[] firstBytes = ReadCache(backend, firstOrder);
        byte[] secondBytes = ReadCache(backend, secondOrder);
        Assert.Equal(finalEnvelope, firstBytes);
        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public void Canonical_float_encoding_unifies_signed_zero_and_all_NaN_payloads()
    {
        Assert.Equal(
            D3D12Backend.CanonicalizePipelineKeySingle(0f),
            D3D12Backend.CanonicalizePipelineKeySingle(-0f));
        Assert.Equal(0u, D3D12Backend.CanonicalizePipelineKeySingle(-0f));

        float positiveNan = BitConverter.UInt32BitsToSingle(0x7FC0_0001u);
        float negativeNan = BitConverter.UInt32BitsToSingle(0xFFC1_2345u);
        Assert.Equal(
            D3D12Backend.CanonicalizePipelineKeySingle(positiveNan),
            D3D12Backend.CanonicalizePipelineKeySingle(negativeNan));
        Assert.Equal(
            0x7FC0_0000u,
            D3D12Backend.CanonicalizePipelineKeySingle(positiveNan));
    }

    [Fact]
    public void Empty_envelope_matches_the_schema_golden_and_corruption_fails_closed()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(device, default);

        byte[] golden = Convert.FromHexString(EmptyEnvelopeGolden);
        Assert.Equal(golden, ReadCache(backend, cache));

        byte[] corrupt = (byte[])golden.Clone();
        corrupt[^1] ^= 0x80;
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corrupt)));

        byte[] unsupported = Convert.FromHexString(UnsupportedSchemaEnvelope);
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(unsupported)));
    }

    [Fact]
    public void Unknown_well_formed_backend_and_family_sections_are_preserved_byte_for_byte()
    {
        byte[] envelope = Convert.FromHexString(UnknownBackendAndFamilyEnvelope);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelope));

        Assert.Equal(envelope, ReadCache(backend, cache));
    }

    [Fact]
    public void Backend_family_and_compatibility_are_independent_section_key_dimensions()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { outputValues[0] = 0xC0FFEEu; }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_cache_backend_identity",
            source,
            [new("computeMain", SlangStage.Compute)]);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache original = backend.CreatePipelineCache(device, default);
        ComputePipelineDesc description = new(shader.Program, shader.GetEntryPoint(0));
        using Pipeline first = backend.CreateComputePipeline(device, description, original);

        byte[] originalEnvelope = ReadCache(backend, original);
        byte[] d3d12Section = GetOnlySection(originalEnvelope);
        Assert.Equal("D3D12\0\0\0"u8.ToArray(), d3d12Section[..8]);
        byte[] unknownBackendSection = ReplaceBackendAndPayload(
            d3d12Section,
            ulong.MaxValue,
            [0xDE, 0xAD, 0xBE, 0xEF]);
        byte[] combinedEnvelope = CreateEnvelope(d3d12Section, unknownBackendSection);

        using PipelineCache combined = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(combinedEnvelope));
        using Pipeline recreated = backend.CreateComputePipeline(device, description, combined);
        Assert.Equal(combinedEnvelope, ReadCache(backend, combined));

        using PipelineCache unknownOnly = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(unknownBackendSection)));
        using Pipeline rebuilt = backend.CreateComputePipeline(device, description, unknownOnly);
        byte[] rebuiltEnvelope = ReadCache(backend, unknownOnly);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(rebuiltEnvelope.AsSpan(12, 4)));

        byte[] unknownFamilySection = (byte[])d3d12Section.Clone();
        unknownFamilySection[8] = byte.MaxValue;
        byte[] unknownFamilyEnvelope = CreateEnvelope(unknownFamilySection);
        using PipelineCache unknownFamily = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(unknownFamilyEnvelope));
        Assert.Equal(unknownFamilyEnvelope, ReadCache(backend, unknownFamily));

        byte[] incompatibleSection = ReplaceBackendAndPayload(
            d3d12Section,
            BinaryPrimitives.ReadUInt64LittleEndian(d3d12Section),
            [0xBA, 0xAD, 0xF0, 0x0D]);
        incompatibleSection[41] ^= 0x80;
        using PipelineCache incompatibleOnly = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(incompatibleSection)));
        using Pipeline rebuiltForLocalCompatibility = backend.CreateComputePipeline(
            device,
            description,
            incompatibleOnly);
        byte[] compatibilityLocalEnvelope = ReadCache(backend, incompatibleOnly);
        Assert.Equal(
            2u,
            BinaryPrimitives.ReadUInt32LittleEndian(compatibilityLocalEnvelope.AsSpan(12, 4)));
    }

    [Fact]
    public void Merge_preserves_unknown_backend_sections_and_remains_order_independent()
    {
        byte[] firstSection = GetOnlySection(
            Convert.FromHexString(UnknownBackendAndFamilyEnvelope));
        byte[] secondSection = ReplaceBackendAndPayload(
            firstSection,
            1,
            [0xCA, 0xFE, 0xBA, 0xBE]);
        secondSection[8]--;
        secondSection[9] ^= 0x80;
        byte[] firstEnvelope = CreateEnvelope(firstSection);
        byte[] secondEnvelope = CreateEnvelope(secondSection);
        byte[] canonical = CreateEnvelope(secondSection, firstSection);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache first = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(firstEnvelope));
        using PipelineCache second = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(secondEnvelope));
        using PipelineCache leftThenRight = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                maximumEntryCount: 2,
                maximumByteCount: canonical.Length,
                maximumDecodedByteCount: 8));
        using PipelineCache rightThenLeft = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(
                maximumEntryCount: 2,
                maximumByteCount: canonical.Length,
                maximumDecodedByteCount: 8));

        backend.MergePipelineCaches(leftThenRight, [first, second]);
        backend.MergePipelineCaches(rightThenLeft, [second, first]);

        Assert.Equal(canonical, ReadCache(backend, leftThenRight));
        Assert.Equal(canonical, ReadCache(backend, rightThenLeft));
    }

    [Fact]
    public void Duplicate_out_of_bounds_section_checksum_and_trailing_bytes_fail_closed()
    {
        byte[] validEnvelope = Convert.FromHexString(UnknownBackendAndFamilyEnvelope);
        byte[] section = GetOnlySection(validEnvelope);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(section, section))));

        byte[] outOfBounds = (byte[])section.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(outOfBounds.AsSpan(73, 4), uint.MaxValue);
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(outOfBounds))));

        byte[] invalidSectionChecksum = (byte[])section.Clone();
        invalidSectionChecksum[^1] ^= 0x80;
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(CreateEnvelope(invalidSectionChecksum))));

        byte[] bodyWithTrailingByte = validEnvelope[..^32].Concat([byte.MaxValue]).ToArray();
        byte[] trailingBytes = AppendEnvelopeChecksum(bodyWithTrailingByte);
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(trailingBytes)));
    }

    [Fact]
    public void Merge_is_order_independent_and_classic_family_entries_survive_a_cross_run_reload()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeA() {}

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeB() {}
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("computeA", SlangStage.Compute),
            new("computeB", SlangStage.Compute),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_compute",
            source,
            entries);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache left = backend.CreatePipelineCache(device, default);
        using PipelineCache right = backend.CreatePipelineCache(device, default);
        using Pipeline leftPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)),
            left);
        using Pipeline rightPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(1)),
            right);

        using PipelineCache leftThenRight = backend.CreatePipelineCache(device, default);
        using PipelineCache rightThenLeft = backend.CreatePipelineCache(device, default);
        backend.MergePipelineCaches(leftThenRight, [left, right]);
        backend.MergePipelineCaches(rightThenLeft, [right, left]);
        byte[] merged = ReadCache(backend, leftThenRight);
        Assert.Equal(merged, ReadCache(backend, rightThenLeft));

        using PipelineCache reloaded = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(merged));
        using Pipeline reloadedA = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)),
            reloaded);
        using Pipeline reloadedB = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(1)),
            reloaded);
        Assert.Equal(merged, ReadCache(backend, reloaded));
    }

    [Fact]
    public void Store_at_capacity_rejects_only_the_new_record_and_preserves_existing_entries()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeA() { outputValues[0] = 11u; }

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeB() {}

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeC() {}
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_capacity_rejection",
            source,
            [
                new D3D12TestShaderEntry("computeA", SlangStage.Compute),
                new D3D12TestShaderEntry("computeB", SlangStage.Compute),
                new D3D12TestShaderEntry("computeC", SlangStage.Compute),
            ]);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        ComputePipelineDesc descA = new(shader.Program, shader.GetEntryPoint(0));
        ComputePipelineDesc descB = new(shader.Program, shader.GetEntryPoint(1));
        ComputePipelineDesc descC = new(shader.Program, shader.GetEntryPoint(2));

        using PipelineCache noAdmissionCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: 48));
        using Pipeline noAdmissionPipeline = backend.CreateComputePipeline(
            device,
            descA,
            noAdmissionCache);
        ExecuteCompute(
            backend,
            device,
            noAdmissionPipeline,
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            11u);
        Assert.Equal(
            Convert.FromHexString(EmptyEnvelopeGolden),
            ReadCache(backend, noAdmissionCache));

        byte[] keyA = CreateComputeKey(backend, device, descA);
        byte[] keyB = CreateComputeKey(backend, device, descB);
        byte[] keyC = CreateComputeKey(backend, device, descC);
        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumEntryCount: 2));
        using Pipeline pipelineA = backend.CreateComputePipeline(device, descA, cache);
        using Pipeline pipelineB = backend.CreateComputePipeline(device, descB, cache);
        byte[] beforeHit = ReadCache(backend, cache);

        using Pipeline hitA = backend.CreateComputePipeline(device, descA, cache);
        byte[] afterHit = ReadCache(backend, cache);
        Assert.Equal(beforeHit, afterHit);

        using Pipeline pipelineC = backend.CreateComputePipeline(device, descC, cache);
        byte[] afterRejectedStore = ReadCache(backend, cache);
        Assert.Equal(beforeHit, afterRejectedStore);
        byte[][] resident = GetSections(afterRejectedStore);
        Assert.Equal(2, resident.Length);
        Assert.Contains(resident, section => GetSectionKey(section).SequenceEqual(keyA));
        Assert.Contains(resident, section => GetSectionKey(section).SequenceEqual(keyB));
        Assert.DoesNotContain(resident, section => GetSectionKey(section).SequenceEqual(keyC));
    }

    [Fact]
    public void Manual_multi_space_interleaved_root_and_static_sampler_survive_cache_reload()
    {
        const string source = """
            Texture2D<float4> sampledTexture : register(t7, space3);
            SamplerState pipelineSampler : register(s4, space2);
            RWStructuredBuffer<uint> outputValues : register(u1, space0);
            ByteAddressBuffer inputValues : register(t9, space1);
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = inputValues.Load<uint>(0)
                    + asuint(sampledTexture.SampleLevel(pipelineSampler, float2(0.5), 0).x);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "manual_multi_space_cache", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = layout.TypeLayout.UnwrapArray();
        nint samplerRange = Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index => (data.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                SlangBindingType.Sampler);
        SamplerDesc sampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        ComputePipelineDesc description = new(shader.Program, shader.GetEntryPoint(0),
            StaticSamplers: new StaticSamplerBinding[]
            {
                new(data.GetBindingRangeLeafVariable(samplerRange), sampler),
            });
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(device, default);
        using Pipeline first = backend.CreateComputePipeline(device, description, cache);
        byte[] root = D3D12Backend.GetSerializedRootSignature(first);
        byte[] envelope = ReadCache(backend, cache);
        using PipelineCache reloaded = backend.CreatePipelineCache(device,
            new PipelineCacheDesc(envelope));
        using Pipeline recreated = backend.CreateComputePipeline(device, description, reloaded);
        Assert.Equal(root, D3D12Backend.GetSerializedRootSignature(recreated));
        Assert.Equal(envelope, ReadCache(backend, reloaded));
    }

    [Fact]
    public void Invalid_native_compute_cache_payload_is_rejected_and_replaced_by_uncached_creation()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { outputValues[0] = 0xC0FFEEu; }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_native_reject_compute",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        ComputePipelineDesc description = new(shader.Program, shader.GetEntryPoint(0));
        using PipelineCache seed = backend.CreatePipelineCache(device, default);
        using (Pipeline pipeline = backend.CreateComputePipeline(device, description, seed)) { }
        byte[] validSection = GetOnlySection(ReadCache(backend, seed));
        byte[] invalidSection = ReplaceBackendAndPayload(
            validSection,
            BinaryPrimitives.ReadUInt64LittleEndian(validSection),
            [0x01]);
        byte[] invalidEnvelope = CreateEnvelope(invalidSection);

        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(invalidEnvelope));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Pipeline recovered = backend.CreateComputePipeline(device, description, cache);
        ExecuteCompute(
            backend,
            device,
            recovered,
            layout,
            output,
            outputUav,
            readback,
            PipelineSync.None,
            ResourceAccess.NoAccess);
        byte[] repaired = ReadCache(backend, cache);
        Assert.NotEqual(invalidEnvelope, repaired);
        Assert.DoesNotContain(GetSections(repaired), section => section.SequenceEqual(invalidSection));

        using Pipeline recreated = backend.CreateComputePipeline(device, description, cache);
        ExecuteCompute(
            backend,
            device,
            recreated,
            layout,
            output,
            outputUav,
            readback,
            PipelineSync.Copy,
            ResourceAccess.CopySource);
    }

    [Fact]
    public void All_five_pipeline_family_envelopes_recreate_pipelines_without_tag_collisions()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        using var backend = new D3D12Backend(new D3D12BackendOptions(validation));
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var familyTags = new List<byte>(5);
        using PipelineCache noAdmissionCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: 48));
        byte[] noAdmissionBytes = ReadCache(backend, noAdmissionCache);

        const string graphicsSource = """
            struct VertexOutput { float4 Position : SV_Position; };
            RWStructuredBuffer<uint> outputValues;
            [shader("vertex")]
            VertexOutput vertexMain(uint id : SV_VertexID)
            {
                VertexOutput value;
                float2 positions[3] = {
                    float2(-1, -1),
                    float2(-1, 3),
                    float2(3, -1)
                };
                value.Position = float4(positions[id], 0, 1);
                return value;
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0
            {
                outputValues[0] = 37u;
                return float4(1, 1, 1, 1);
            }
            """;
        using D3D12TestShaderProgram graphicsShader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_graphics",
            graphicsSource,
            [
                new D3D12TestShaderEntry("vertexMain", SlangStage.Vertex),
                new D3D12TestShaderEntry("pixelMain", SlangStage.Fragment),
            ]);
        GraphicsPipelineDesc graphicsDescription = new(
            graphicsShader.Program,
            graphicsShader.GetEntryPoint(0),
            graphicsShader.GetEntryPoint(1),
            [],
            [],
            PrimitiveTopology.TriangleList,
            StripCut.Disabled,
            new RasterizerState(Cull: CullType.None),
            new MultisampleState(SampleCount: 1),
            new DepthStencilState(),
            new BlendState([new BlendAttachmentState(WriteMask: ColorWriteMasks.All)]),
            new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null));
        using PipelineCache graphicsCache = backend.CreatePipelineCache(device, default);
        using Pipeline graphicsPipeline = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription,
            graphicsCache);
        byte[] graphicsEnvelope = ReadCache(backend, graphicsCache);
        familyTags.Add(GetOnlySection(graphicsEnvelope)[8]);
        byte[] corruptGraphicsEnvelope = CorruptClassicPayload(graphicsEnvelope);
        using PipelineCache reloadedGraphicsCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corruptGraphicsEnvelope));
        using Pipeline recreatedGraphicsPipeline = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription,
            reloadedGraphicsCache);
        ExecuteGraphics(
            backend,
            device,
            recreatedGraphicsPipeline,
            graphicsShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.NotEqual(corruptGraphicsEnvelope, ReadCache(backend, reloadedGraphicsCache));
        using Pipeline replayedGraphicsPipeline = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription,
            reloadedGraphicsCache);
        ExecuteGraphics(
            backend,
            device,
            replayedGraphicsPipeline,
            graphicsShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        using Pipeline noAdmissionGraphics = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription,
            noAdmissionCache);
        ExecuteGraphics(
            backend,
            device,
            noAdmissionGraphics,
            graphicsShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.Equal(noAdmissionBytes, ReadCache(backend, noAdmissionCache));

        const string computeSource = """
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { outputValues[0] = 41u; }
            """;
        using D3D12TestShaderProgram computeShader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_all_families_compute",
            computeSource,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        ComputePipelineDesc computeDescription = new(
            computeShader.Program,
            computeShader.GetEntryPoint(0));
        using PipelineCache computeCache = backend.CreatePipelineCache(device, default);
        using Pipeline computePipeline = backend.CreateComputePipeline(
            device,
            computeDescription,
            computeCache);
        byte[] computeEnvelope = ReadCache(backend, computeCache);
        familyTags.Add(GetOnlySection(computeEnvelope)[8]);
        using PipelineCache reloadedComputeCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(computeEnvelope));
        using Pipeline recreatedComputePipeline = backend.CreateComputePipeline(
            device,
            computeDescription,
            reloadedComputeCache);
        Assert.Equal(computeEnvelope, ReadCache(backend, reloadedComputeCache));
        using Pipeline noAdmissionCompute = backend.CreateComputePipeline(
            device,
            computeDescription,
            noAdmissionCache);
        ExecuteCompute(
            backend,
            device,
            noAdmissionCompute,
            computeShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            41u);
        Assert.Equal(noAdmissionBytes, ReadCache(backend, noAdmissionCache));

        Assert.True(
            backend.TryGetCapability(device, out MeshShaders? meshShaders),
            "Mesh-family native cache recreation remains unexecuted because this WARP Device does not report MeshShaders.");
        Assert.NotNull(meshShaders);
        byte[] meshEnvelope = CreateAndRecreateMeshFamily(backend, device);
        familyTags.Add(GetOnlySection(meshEnvelope)[8]);

        Assert.True(backend.TryGetCapability(device, out RayTracing? rayTracing));
        Assert.NotNull(rayTracing);
        const string raySource = """
            RWStructuredBuffer<uint> outputValues;
            [shader("raygeneration")]
            void rayGenerationMain() { outputValues[0] = 73u; }
            """;
        using D3D12TestShaderProgram rayShader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_ray",
            raySource,
            [new D3D12TestShaderEntry("rayGenerationMain", SlangStage.RayGeneration)]);
        EntryPointReflection[] rayGeneration = [rayShader.GetEntryPoint(0)];
        RayTracingPipelineDesc rayDescription = new(
            rayShader.Program,
            rayGeneration,
            [],
            [],
            [],
            1,
            0,
            8);
        using PipelineCache rayCache = backend.CreatePipelineCache(device, default);
        using Pipeline rayPipeline = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            rayCache);
        byte[] rayEnvelope = ReadCache(backend, rayCache);
        familyTags.Add(GetOnlySection(rayEnvelope)[8]);
        byte[] corruptRayEnvelope = CorruptReplayPayload(rayEnvelope);
        using PipelineCache reloadedRayCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corruptRayEnvelope));
        using Pipeline recreatedRayPipeline = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            reloadedRayCache);
        ExecuteRayTracing(
            backend,
            device,
            recreatedRayPipeline,
            rayGeneration[0],
            rayShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.NotEqual(corruptRayEnvelope, ReadCache(backend, reloadedRayCache));
        using Pipeline replayedRayPipeline = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            reloadedRayCache);
        ExecuteRayTracing(
            backend,
            device,
            replayedRayPipeline,
            rayGeneration[0],
            rayShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        using Pipeline noAdmissionRay = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            noAdmissionCache);
        ExecuteRayTracing(
            backend,
            device,
            noAdmissionRay,
            rayGeneration[0],
            rayShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.Equal(noAdmissionBytes, ReadCache(backend, noAdmissionCache));

        Assert.True(backend.TryGetCapability(device, out WorkGraphs? workGraphs));
        Assert.NotNull(workGraphs);
        const string workGraphSource = """
            import experimental.workgraph;
            struct WorkRecord { uint Remaining; };
            RWStructuredBuffer<uint> outputValues;

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeMaxRecursionDepth(1)]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphMain(DispatchNodeInputRecord<WorkRecord> input)
            {
                outputValues[0] = input.Get().Remaining;
            }
            """;
        using D3D12TestShaderProgram graphShader =
            D3D12TestShaderProgram.CompileExperimental(
                "rhi_pipeline_cache_work_graph",
                workGraphSource,
                [new D3D12TestShaderEntry("graphMain", SlangStage.Node)]);
        WorkGraphPipelineDesc graphDescription = new(graphShader.Program);
        using PipelineCache graphCache = backend.CreatePipelineCache(device, default);
        using Pipeline graphPipeline = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            graphCache);
        byte[] graphEnvelope = ReadCache(backend, graphCache);
        familyTags.Add(GetOnlySection(graphEnvelope)[8]);
        byte[] corruptGraphEnvelope = CorruptReplayPayload(graphEnvelope);
        using PipelineCache reloadedGraphCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corruptGraphEnvelope));
        using Pipeline recreatedGraphPipeline = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            reloadedGraphCache);
        ExecuteWorkGraph(
            backend,
            device,
            recreatedGraphPipeline,
            graphShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.NotEqual(corruptGraphEnvelope, ReadCache(backend, reloadedGraphCache));
        using Pipeline replayedGraphPipeline = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            reloadedGraphCache);
        ExecuteWorkGraph(
            backend,
            device,
            replayedGraphPipeline,
            graphShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        using Pipeline noAdmissionGraph = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            noAdmissionCache);
        ExecuteWorkGraph(
            backend,
            device,
            noAdmissionGraph,
            graphShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.Equal(noAdmissionBytes, ReadCache(backend, noAdmissionCache));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, familyTags);
        Assert.Equal(5, familyTags.Distinct().Count());
    }

    private static byte[] CreateAndRecreateMeshFamily(
        D3D12Backend backend,
        Device device)
    {
        const string meshSource = """
            struct MeshVertex { float4 Position : SV_Position; };
            RWStructuredBuffer<uint> outputValues;

            [shader("mesh")]
            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void meshMain(
                out vertices MeshVertex outputVertices[3],
                out indices uint3 outputTriangles[1])
            {
                SetMeshOutputCounts(3, 1);
                outputVertices[0].Position = float4(-1.0, -1.0, 0.0, 1.0);
                outputVertices[1].Position = float4(0.0, 1.0, 0.0, 1.0);
                outputVertices[2].Position = float4(1.0, -1.0, 0.0, 1.0);
                outputTriangles[0] = uint3(0, 1, 2);
            }

            [shader("pixel")]
            float4 pixelMain(float4 position : SV_Position) : SV_Target0
            {
                outputValues[0] = 109u;
                return float4(0.25, 0.5, 0.75, 1.0);
            }
            """;
        using D3D12TestShaderProgram meshShader =
            D3D12TestShaderProgram.Compile(
                "rhi_pipeline_cache_mesh",
                meshSource,
                [
                    new D3D12TestShaderEntry("meshMain", SlangStage.Mesh),
                    new D3D12TestShaderEntry("pixelMain", SlangStage.Fragment),
                ]);
        MeshPipelineDesc meshDescription = new(
            meshShader.Program,
            meshShader.GetEntryPoint(0),
            EntryPointReflection.Null,
            meshShader.GetEntryPoint(1),
            new RasterizerState(Cull: CullType.None),
            new MultisampleState(SampleCount: 1),
            new DepthStencilState(),
            new BlendState([new BlendAttachmentState(WriteMask: ColorWriteMasks.All)]),
            new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null));
        using PipelineCache meshCache = backend.CreatePipelineCache(device, default);
        using Pipeline meshPipeline = backend.CreateMeshPipeline(
            device,
            meshDescription,
            meshCache);
        byte[] meshEnvelope = ReadCache(backend, meshCache);
        byte[] corruptMeshEnvelope = CorruptClassicPayload(meshEnvelope);
        using PipelineCache reloadedMeshCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corruptMeshEnvelope));
        using Pipeline recreatedMeshPipeline = backend.CreateMeshPipeline(
            device,
            meshDescription,
            reloadedMeshCache);
        ExecuteMesh(
            backend,
            device,
            recreatedMeshPipeline,
            meshShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.NotEqual(corruptMeshEnvelope, ReadCache(backend, reloadedMeshCache));
        using Pipeline replayedMeshPipeline = backend.CreateMeshPipeline(
            device,
            meshDescription,
            reloadedMeshCache);
        ExecuteMesh(
            backend,
            device,
            replayedMeshPipeline,
            meshShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        using PipelineCache noAdmissionCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(maximumByteCount: 48));
        byte[] before = ReadCache(backend, noAdmissionCache);
        using Pipeline noAdmissionPipeline = backend.CreateMeshPipeline(
            device,
            meshDescription,
            noAdmissionCache);
        ExecuteMesh(
            backend,
            device,
            noAdmissionPipeline,
            meshShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        Assert.Equal(before, ReadCache(backend, noAdmissionCache));
        return meshEnvelope;
    }

    private static void ExecuteMesh(
        D3D12Backend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection globals)
    {
        TextureSubresourceRange targetRange = new(0, 1, 0, 1, TextureAspects.Color);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                targetRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        ColorAttachmentDesc[] colors =
        [
            new(targetView, LoadType.Clear, StoreType.Store, new System.Numerics.Vector4(0, 0, 0, 1)),
        ];
        backend.Begin(context, new CommandRecordingDesc(8, 2, 32));
        backend.Barrier(context, new TextureBarrier(
            target,
            targetRange,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.PixelShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 8, 8)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 8, 8)]);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.BeginRendering(context, new RenderingDesc(colors, null, 8, 8));
        backend.DispatchMesh(context, new DispatchArguments(1, 1, 1));
        backend.EndRendering(context);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.PixelShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(109u, MemoryMarshal.Read<uint>(mapped.Bytes));
        backend.CollectCompleted(device);
    }

    private static void ExecuteGraphics(
        D3D12Backend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection globals)
    {
        TextureSubresourceRange targetRange = new(0, 1, 0, 1, TextureAspects.Color);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                targetRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        ColorAttachmentDesc[] colors =
        [
            new(targetView, LoadType.Clear, StoreType.Store, new System.Numerics.Vector4(0, 0, 0, 1)),
        ];
        backend.Begin(context, new CommandRecordingDesc(8, 2, 32));
        backend.Barrier(context, new TextureBarrier(
            target,
            targetRange,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.PixelShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 8, 8)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 8, 8)]);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.BeginRendering(context, new RenderingDesc(colors, null, 8, 8));
        backend.Draw(context, new DrawArguments(3, 1, 0, 0));
        backend.Draw(context, new DrawArguments(3, 1, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.PixelShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(37u, MemoryMarshal.Read<uint>(mapped.Bytes));
        backend.CollectCompleted(device);
    }

    private static void ExecuteCompute(
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        uint expected)
    {
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        ExecuteCompute(
            backend,
            device,
            pipeline,
            layout,
            output,
            outputUav,
            readback,
            PipelineSync.None,
            ResourceAccess.NoAccess,
            expected);
    }

    private static void ExecuteCompute(
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        Buffer output,
        BufferUav outputUav,
        Buffer readback,
        PipelineSync beforeSync,
        ResourceAccess beforeAccess,
        uint expected = 0xC0FFEEu)
    {
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        backend.Barrier(context, new BufferBarrier(
            output,
            beforeSync,
            PipelineSync.ComputeShading,
            beforeAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                layout,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(expected, MemoryMarshal.Read<uint>(mapped.Bytes));
        backend.CollectCompleted(device);
    }

    private static void ExecuteRayTracing(
        D3D12Backend backend,
        Device device,
        Pipeline pipeline,
        EntryPointReflection rayGeneration,
        VariableLayoutReflection globals)
    {
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 0, 32));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 8, 64));
        backend.SetPipeline(context, pipeline);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.RayTracing,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.UpdateRayTracingShaderTable(
            context,
            table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(rayGeneration, 0, 1)],
                [], [], [],
                [new RayTracingLocalParameterBlock(rayGeneration.VarLayout, 0, 0, 0, 0)],
                [], []));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.RayTracing,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(73u, MemoryMarshal.Read<uint>(mapped.Bytes));
        backend.CollectCompleted(device);
    }

    private static void ExecuteWorkGraph(
        D3D12Backend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection globals)
    {
        var entryPoints = new WorkGraphEntryPointInfo[1];
        Assert.True(backend.TryGetWorkGraphEntryPoints(pipeline, entryPoints, out int count));
        Assert.Equal(1, count);
        Assert.Equal("graphMain", entryPoints[0].EntryPoint.Name);
        Assert.True(entryPoints[0].RecordSize >= sizeof(uint));
        byte[] record = new byte[checked((int)entryPoints[0].RecordSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 91u);
        WorkGraphMemoryRequirements requirements = backend.GetWorkGraphMemoryRequirements(pipeline);
        using Buffer? backing = requirements.MinimumSize == 0
            ? null
            : backend.CreateBuffer(
                device,
                new BufferDesc(requirements.MinimumSize, BufferUsages.ShaderWrite),
                MemoryType.DeviceLocal);
        BufferRegion? backingRegion = backing is null ? null : new BufferRegion(backing, BufferRange.Whole);
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 2, 32));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        if (backing is not null)
        {
            backend.Barrier(context, new BufferBarrier(
                backing,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
        }
        backend.BindWorkGraph(
            context,
            pipeline,
            backingRegion,
            WorkGraphInitialization.Initialize);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.DispatchWorkGraph(
            context,
            new WorkGraphDispatchDesc(
                entryPoints[0].EntryPoint,
                record,
                1,
                entryPoints[0].RecordSize));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(91u, MemoryMarshal.Read<uint>(mapped.Bytes));
        backend.CollectCompleted(device);
    }

    private static byte[] CorruptReplayPayload(ReadOnlySpan<byte> envelope)
    {
        byte[] section = GetOnlySection(envelope);
        int payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(73, 4)));
        byte[] payload = section.AsSpan(77, payloadLength).ToArray();
        Assert.NotEmpty(payload);
        payload[0] ^= 0x80;
        return CreateEnvelope(ReplaceBackendAndPayload(
            section,
            BinaryPrimitives.ReadUInt64LittleEndian(section),
            payload));
    }

    private static byte[] CorruptClassicPayload(ReadOnlySpan<byte> envelope) =>
        CorruptReplayPayload(envelope);

    private static byte[] CreateComputeKey(
        IGraphicsBackend backend,
        Device device,
        in ComputePipelineDesc desc)
    {
        using PipelineCache cache = backend.CreatePipelineCache(device, default);
        using Pipeline pipeline = backend.CreateComputePipeline(device, desc, cache);
        return GetSectionKey(GetOnlySection(ReadCache(backend, cache)));
    }

    private static byte[] ReadCache(IGraphicsBackend backend, PipelineCache cache)
    {
        Assert.False(backend.TryGetPipelineCacheData(cache, [], out int required));
        Assert.True(required > 0);
        byte[] data = new byte[required];
        Assert.True(backend.TryGetPipelineCacheData(cache, data, out int confirmed));
        Assert.Equal(data.Length, confirmed);
        return data;
    }

    private static byte[] GetOnlySection(ReadOnlySpan<byte> envelope)
    {
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(envelope.Slice(12, 4)));
        return envelope.Slice(16, envelope.Length - 16 - 32).ToArray();
    }

    private static byte[][] GetSections(ReadOnlySpan<byte> envelope)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(envelope.Slice(12, 4)));
        var result = new byte[count][];
        int offset = 16;
        for (int index = 0; index < count; index++)
        {
            int payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                envelope.Slice(offset + 73, 4)));
            int sectionLength = checked(109 + payloadLength);
            result[index] = envelope.Slice(offset, sectionLength).ToArray();
            offset = checked(offset + sectionLength);
        }
        Assert.Equal(envelope.Length - 32, offset);
        return result;
    }

    private static byte[] GetSectionKey(ReadOnlySpan<byte> section) =>
        section.Slice(9, 32).ToArray();

    private static byte[] CreateUnknownSection(byte keySuffix, ReadOnlySpan<byte> payload)
    {
        byte[] section = GetOnlySection(Convert.FromHexString(UnknownBackendAndFamilyEnvelope));
        section.AsSpan(9, 32).Clear();
        section[40] = keySuffix;
        return ReplaceBackendAndPayload(
            section,
            BinaryPrimitives.ReadUInt64LittleEndian(section),
            payload);
    }

    private static byte[] CreateUnknownSectionWithKey(
        uint key,
        ReadOnlySpan<byte> payload)
    {
        byte[] section = GetOnlySection(Convert.FromHexString(UnknownBackendAndFamilyEnvelope));
        section.AsSpan(9, 32).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(37, 4), key);
        return ReplaceBackendAndPayload(
            section,
            BinaryPrimitives.ReadUInt64LittleEndian(section),
            payload);
    }

    private static byte[] CreatePayload(int length)
    {
        var result = new byte[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = unchecked((byte)(index * 131 + length));
        return result;
    }

    private static byte[] ReplaceBackendAndPayload(
        ReadOnlySpan<byte> section,
        ulong backend,
        ReadOnlySpan<byte> payload)
    {
        const int payloadLengthOffset = 73;
        const int payloadOffset = payloadLengthOffset + 4;
        byte[] result = new byte[payloadOffset + payload.Length + 32];
        section[..payloadLengthOffset].CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(result, backend);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(payloadLengthOffset, 4),
            checked((uint)payload.Length));
        payload.CopyTo(result.AsSpan(payloadOffset));
        SHA256.HashData(payload).CopyTo(result.AsSpan(payloadOffset + payload.Length));
        return result;
    }

    private static byte[] CreateEnvelope(params byte[][] sections)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SERHIC01"u8);
            writer.Write(3u);
            writer.Write(checked((uint)sections.Length));
            foreach (byte[] section in sections)
                writer.Write(section);
        }
        return AppendEnvelopeChecksum(stream.ToArray());
    }

    private static byte[] AppendEnvelopeChecksum(ReadOnlySpan<byte> body)
    {
        byte[] result = new byte[body.Length + 32];
        body.CopyTo(result);
        SHA256.HashData(body).CopyTo(result.AsSpan(body.Length));
        return result;
    }

    private static byte[] CreateCountOnlyEnvelope(uint count)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SERHIC01"u8);
            writer.Write(3u);
            writer.Write(count);
        }
        return AppendEnvelopeChecksum(stream.ToArray());
    }
}

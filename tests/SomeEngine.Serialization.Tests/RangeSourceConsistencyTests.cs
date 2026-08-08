using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class RangeSourceConsistencyTests
{
    [Fact]
    public async Task MemorySourceHasInstanceGenerationAndBorrowedLeaseOutlivesSource()
    {
        byte[] callerBytes = Enumerable.Range(0, 64).Select(static value => (byte)(value * 3)).ToArray();
        await using var equivalentSource = new MemoryRangeSource(callerBytes);
        var source = new MemoryRangeSource(callerBytes);
        string generation = source.Generation;
        RangeLease lease = await source.AcquireAsync(11, 17);

        callerBytes.AsSpan().Fill(0xCC);
        await source.DisposeAsync();

        Assert.NotEqual(equivalentSource.Generation, generation);
        Assert.StartsWith("memory:", equivalentSource.Generation, StringComparison.Ordinal);
        Assert.StartsWith("memory:", generation, StringComparison.Ordinal);
        Assert.Equal(generation, source.Generation);
        Assert.Equal(Enumerable.Repeat((byte)0xCC, 17), lease.Memory.ToArray());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadExactlyAsync(0, new byte[1]).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.AcquireAsync(0, 1).AsTask());

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Memory);
    }

    [Fact]
    public async Task FileSourcePinsOpenedHandleAcrossPathReplacementAndUsesExplicitOffsets()
    {
        byte[] originalBytes = Enumerable.Range(0, 4096)
            .Select(static value => (byte)(value * 17 + 3))
            .ToArray();
        byte[] replacementBytes = Enumerable.Range(0, 4096)
            .Select(static value => (byte)(255 - (value * 29 & 0xFF)))
            .ToArray();
        string path = Path.Combine(AppContext.BaseDirectory, $"file-range-{Guid.NewGuid():N}.bin");
        string movedPath = path + ".opened";
        await File.WriteAllBytesAsync(path, originalBytes);

        FileRangeSource? originalSource = null;
        FileRangeSource? replacementSource = null;
        RangeLease? lease = null;
        try
        {
            originalSource = FileRangeSource.Open(path);
            string openedGeneration = originalSource.Generation;
            File.Move(path, movedPath);
            await File.WriteAllBytesAsync(path, replacementBytes);
            replacementSource = FileRangeSource.Open(path);

            byte[] highRange = new byte[113];
            byte[] lowRange = new byte[79];
            await Task.WhenAll(
                originalSource.ReadExactlyAsync(3001, highRange).AsTask(),
                originalSource.ReadExactlyAsync(37, lowRange).AsTask());
            lease = await originalSource.AcquireAsync(997, 211);

            Assert.Equal(originalBytes.AsSpan(3001, highRange.Length).ToArray(), highRange);
            Assert.Equal(originalBytes.AsSpan(37, lowRange.Length).ToArray(), lowRange);
            Assert.Equal(originalBytes.AsSpan(997, 211).ToArray(), lease.Memory.ToArray());
            Assert.Equal(openedGeneration, originalSource.Generation);
            Assert.NotEqual(originalSource.Generation, replacementSource.Generation);
            Assert.True(originalSource.LeasesAreImmutable);

            byte[] replacementRange = new byte[113];
            await replacementSource.ReadExactlyAsync(3001, replacementRange);
            Assert.Equal(replacementBytes.AsSpan(3001, replacementRange.Length).ToArray(), replacementRange);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                originalSource.ReadExactlyAsync(0, Memory<byte>.Empty, canceled.Token).AsTask());

            await originalSource.DisposeAsync();
            Assert.Equal(originalBytes.AsSpan(997, 211).ToArray(), lease.Memory.ToArray());
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                originalSource.ReadExactlyAsync(0, new byte[1]).AsTask());
        }
        finally
        {
            lease?.Dispose();
            if (replacementSource is not null)
                await replacementSource.DisposeAsync();
            if (originalSource is not null)
                await originalSource.DisposeAsync();
            File.Delete(path);
            File.Delete(movedPath);
        }
    }

    [Fact]
    public async Task MemoryMappedLeaseIsMappedMemoryAndOwnsItsViewLifetime()
    {
        byte[] bytes = Enumerable.Range(0, 4096)
            .Select(static value => (byte)(value * 31 + 7))
            .ToArray();
        string path = Path.Combine(AppContext.BaseDirectory, $"mmap-range-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, bytes);

        MemoryMappedRangeSource? source = null;
        RangeLease? lease = null;
        try
        {
            source = MemoryMappedRangeSource.Open(path);
            string generation = source.Generation;
            lease = await source.AcquireAsync(257, 777);
            ReadOnlyMemory<byte> mappedMemory = lease.Memory;

            Assert.False(MemoryMarshal.TryGetArray(mappedMemory, out _));
            Assert.True(MemoryMarshal.TryGetMemoryManager<byte, MemoryManager<byte>>(
                mappedMemory,
                out MemoryManager<byte>? manager,
                out int managerStart,
                out int managerLength));
            Assert.NotNull(manager);
            Assert.Equal(0, managerStart);
            Assert.Equal(777, managerLength);
            Assert.True(source.LeasesAreImmutable);
            Assert.True(source.RetainsResidentBacking);

            await source.DisposeAsync();

            Assert.Equal(generation, source.Generation);
            Assert.Equal(bytes.AsSpan(257, 777).ToArray(), mappedMemory.ToArray());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.AcquireAsync(0, 1).AsTask());

            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Memory);
            Assert.Throws<ObjectDisposedException>(() => ReadFirstByte(mappedMemory));
        }
        finally
        {
            lease?.Dispose();
            if (source is not null)
                await source.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemoryMappedSourceSupportsAnEmptyFileAndRejectsUseAfterDispose()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"mmap-empty-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, []);

        try
        {
            var source = MemoryMappedRangeSource.Open(path);
            Assert.Equal(0, source.Length);
            using RangeLease empty = await source.AcquireAsync(0, 0);
            Assert.True(empty.Memory.IsEmpty);

            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.AcquireAsync(0, 0).AsTask());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HeadValidator.Missing)]
    [InlineData(HeadValidator.Weak)]
    public async Task HttpOpenRequiresStrongHeadEtag(HeadValidator validator)
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            HeadValidator = validator,
        };
        using var client = new HttpClient(handler);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpRangeSource.OpenAsync(client, AssetUri).AsTask());

        Assert.Contains("strong ETag", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.HeadRequests);
        Assert.Equal(0, handler.GetRequests);
    }

    [Fact]
    public async Task HttpOpenRequiresContentLengthForThePinnedRepresentation()
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            OmitHeadContentLength = true,
        };
        using var client = new HttpClient(handler);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpRangeSource.OpenAsync(client, AssetUri).AsTask());

        Assert.Contains("Content-Length", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpRangePinsGenerationWithIfMatchAndAcceptsExactPartialBody()
    {
        byte[] content = CreateHttpContent();
        using var handler = new ScriptedRangeHandler(content);
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri);
        byte[] destination = new byte[17];

        await source.ReadExactlyAsync(23, destination);

        Assert.Equal(content.AsSpan(23, 17).ToArray(), destination);
        Assert.Equal(StrongEtag, handler.ObservedIfMatch);
        Assert.Equal("bytes=23-39", handler.ObservedRange);
        Assert.Equal(1, handler.GetRequests);
        Assert.True(source.LeasesAreImmutable);
    }

    [Theory]
    [InlineData(GetValidator.Missing)]
    [InlineData(GetValidator.Weak)]
    [InlineData(GetValidator.Changed)]
    public async Task HttpRangeRequiresEveryGetToRepeatOpenedStrongEtag(GetValidator validator)
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            GetValidator = validator,
        };
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            source.ReadExactlyAsync(3, new byte[9]).AsTask());

        Assert.Contains("ETag", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RangeMetadata.Missing)]
    [InlineData(RangeMetadata.WrongUnit)]
    [InlineData(RangeMetadata.WrongStart)]
    [InlineData(RangeMetadata.WrongEnd)]
    [InlineData(RangeMetadata.WrongTotalLength)]
    public async Task HttpRangeRejectsInexactContentRange(RangeMetadata metadata)
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            RangeMetadata = metadata,
        };
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            source.ReadExactlyAsync(5, new byte[11]).AsTask());

        Assert.Contains("Content-Range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BodyShape.Truncated, typeof(EndOfStreamException))]
    [InlineData(BodyShape.Excess, typeof(IOException))]
    public async Task HttpRangeRejectsTruncatedAndExcessBodies(BodyShape bodyShape, Type exceptionType)
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            BodyShape = bodyShape,
            OmitGetContentLength = true,
        };
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri);

        Exception error = await Record.ExceptionAsync(() =>
            source.ReadExactlyAsync(7, new byte[13]).AsTask())
            ?? throw new Xunit.Sdk.XunitException("Malformed HTTP body was unexpectedly accepted.");

        Assert.IsType(exceptionType, error);
    }

    [Fact]
    public async Task HttpRangeRejectsADeclaredLengthThatDoesNotMatchTheRequestedRange()
    {
        using var handler = new ScriptedRangeHandler(CreateHttpContent())
        {
            DeclaredGetLengthDelta = 1,
        };
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            source.ReadExactlyAsync(7, new byte[13]).AsTask());

        Assert.Contains("declared", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13-byte range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpRangeRejectsIfMatchFailureAndNonPartialSuccess()
    {
        using (var preconditionHandler = new ScriptedRangeHandler(CreateHttpContent())
        {
            GetStatusCode = HttpStatusCode.PreconditionFailed,
        })
        using (var client = new HttpClient(preconditionHandler))
        await using (HttpRangeSource source = await HttpRangeSource.OpenAsync(client, AssetUri))
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                source.ReadExactlyAsync(0, new byte[1]).AsTask());
            Assert.Contains("generation changed", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var fullBodyHandler = new ScriptedRangeHandler(CreateHttpContent())
        {
            GetStatusCode = HttpStatusCode.OK,
        };
        using var fullBodyClient = new HttpClient(fullBodyHandler);
        await using HttpRangeSource fullBodySource = await HttpRangeSource.OpenAsync(fullBodyClient, AssetUri);
        IOException statusError = await Assert.ThrowsAsync<IOException>(() =>
            fullBodySource.ReadExactlyAsync(0, new byte[1]).AsTask());
        Assert.Contains("206", statusError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpOpenAndRangeReadsHonorCancellationIncludingEmptyRanges()
    {
        using (var delayedHeadHandler = new ScriptedRangeHandler(CreateHttpContent()) { DelayHead = true })
        using (var client = new HttpClient(delayedHeadHandler))
        using (var canceledOpen = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                HttpRangeSource.OpenAsync(client, AssetUri, cancellationToken: canceledOpen.Token).AsTask());
        }

        using var delayedGetHandler = new ScriptedRangeHandler(CreateHttpContent()) { DelayGet = true };
        using var delayedGetClient = new HttpClient(delayedGetHandler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(delayedGetClient, AssetUri);
        using (var canceledRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                source.ReadExactlyAsync(0, new byte[1], canceledRead.Token).AsTask());
        }

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        int requestsBeforeEmptyRead = delayedGetHandler.GetRequests;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.ReadExactlyAsync(0, Memory<byte>.Empty, alreadyCanceled.Token).AsTask());
        Assert.Equal(requestsBeforeEmptyRead, delayedGetHandler.GetRequests);
    }

    private static readonly Uri AssetUri = new("https://assets.example.test/content.bin");
    private const string StrongEtag = "\"stable-generation\"";

    private static byte[] CreateHttpContent()
        => Enumerable.Range(0, 128).Select(static value => (byte)(value * 7 + 1)).ToArray();

    private static byte ReadFirstByte(ReadOnlyMemory<byte> memory) => memory.Span[0];

    public enum HeadValidator
    {
        Strong,
        Missing,
        Weak,
    }

    public enum GetValidator
    {
        Strong,
        Missing,
        Weak,
        Changed,
    }

    public enum RangeMetadata
    {
        Valid,
        Missing,
        WrongUnit,
        WrongStart,
        WrongEnd,
        WrongTotalLength,
    }

    public enum BodyShape
    {
        Exact,
        Truncated,
        Excess,
    }

    private sealed class ScriptedRangeHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private int _headRequests;
        private int _getRequests;

        internal ScriptedRangeHandler(byte[] content) => _content = content;

        internal HeadValidator HeadValidator { get; init; } = HeadValidator.Strong;
        internal GetValidator GetValidator { get; init; } = GetValidator.Strong;
        internal RangeMetadata RangeMetadata { get; init; } = RangeMetadata.Valid;
        internal BodyShape BodyShape { get; init; } = BodyShape.Exact;
        internal HttpStatusCode GetStatusCode { get; init; } = HttpStatusCode.PartialContent;
        internal bool OmitHeadContentLength { get; init; }
        internal bool OmitGetContentLength { get; init; }
        internal int DeclaredGetLengthDelta { get; init; }
        internal bool DelayHead { get; init; }
        internal bool DelayGet { get; init; }
        internal int HeadRequests => Volatile.Read(ref _headRequests);
        internal int GetRequests => Volatile.Read(ref _getRequests);
        internal string? ObservedIfMatch { get; private set; }
        internal string? ObservedRange { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref _headRequests);
                if (DelayHead)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CreateHeadResponse(request);
            }

            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException($"Unexpected method {request.Method}.");

            Interlocked.Increment(ref _getRequests);
            ObservedIfMatch = request.Headers.IfMatch.SingleOrDefault()?.Tag;
            ObservedRange = request.Headers.Range?.ToString();
            if (DelayGet)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateGetResponse(request);
        }

        private HttpResponseMessage CreateHeadResponse(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
                RequestMessage = request,
            };
            response.Headers.ETag = HeadValidator switch
            {
                HeadValidator.Strong => new EntityTagHeaderValue(StrongEtag),
                HeadValidator.Weak => new EntityTagHeaderValue(StrongEtag, isWeak: true),
                HeadValidator.Missing => null,
                _ => throw new InvalidOperationException(),
            };
            if (OmitHeadContentLength)
                response.Content.Headers.ContentLength = null;
            else
                response.Content.Headers.ContentLength = _content.LongLength;
            return response;
        }

        private HttpResponseMessage CreateGetResponse(HttpRequestMessage request)
        {
            if (GetStatusCode != HttpStatusCode.PartialContent)
            {
                return new HttpResponseMessage(GetStatusCode)
                {
                    Content = new ByteArrayContent([]),
                    RequestMessage = request,
                };
            }

            RangeItemHeaderValue requested = request.Headers.Range?.Ranges.Single()
                ?? throw new InvalidOperationException("GET omitted its byte range.");
            long from = requested.From ?? throw new InvalidOperationException("GET range omitted its start.");
            long to = requested.To ?? throw new InvalidOperationException("GET range omitted its end.");
            int requestedLength = checked((int)(to - from + 1));
            byte[] body = BodyShape switch
            {
                BodyShape.Exact => _content.AsSpan(checked((int)from), requestedLength).ToArray(),
                BodyShape.Truncated => _content.AsSpan(checked((int)from), requestedLength - 1).ToArray(),
                BodyShape.Excess => [.. _content.AsSpan(checked((int)from), requestedLength), 0xEE],
                _ => throw new InvalidOperationException(),
            };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };
            response.Headers.ETag = GetValidator switch
            {
                GetValidator.Strong => new EntityTagHeaderValue(StrongEtag),
                GetValidator.Weak => new EntityTagHeaderValue(StrongEtag, isWeak: true),
                GetValidator.Changed => new EntityTagHeaderValue("\"changed-generation\""),
                GetValidator.Missing => null,
                _ => throw new InvalidOperationException(),
            };
            response.Content.Headers.ContentRange = CreateContentRange(from, to);
            if (OmitGetContentLength)
                response.Content.Headers.ContentLength = null;
            else if (DeclaredGetLengthDelta != 0)
                response.Content.Headers.ContentLength = checked(requestedLength + DeclaredGetLengthDelta);
            return response;
        }

        private ContentRangeHeaderValue? CreateContentRange(long from, long to)
        {
            if (RangeMetadata == RangeMetadata.Missing)
                return null;

            long actualFrom = RangeMetadata == RangeMetadata.WrongStart ? from + 1 : from;
            long actualTo = RangeMetadata == RangeMetadata.WrongEnd ? to - 1 : to;
            long actualLength = RangeMetadata == RangeMetadata.WrongTotalLength
                ? _content.LongLength + 1
                : _content.LongLength;
            var range = new ContentRangeHeaderValue(actualFrom, actualTo, actualLength);
            if (RangeMetadata == RangeMetadata.WrongUnit)
                range.Unit = "items";
            return range;
        }
    }
}

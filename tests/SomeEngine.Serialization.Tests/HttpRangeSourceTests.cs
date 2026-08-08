using System.Net;
using System.Net.Http.Headers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class HttpRangeSourceTests
{
    [Fact]
    public async Task PartialContentWithEquivalentStrongEtagInstanceSucceeds()
    {
        byte[] content = Enumerable.Range(0, 32).Select(static value => (byte)(value * 7)).ToArray();
        using var handler = new EquivalentEtagRangeHandler(content);
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(
            client,
            new Uri("https://assets.example.test/archive.bin"));
        byte[] destination = new byte[9];

        await source.ReadExactlyAsync(offset: 7, destination);

        Assert.Equal(content.AsSpan(7, 9).ToArray(), destination);
        Assert.Equal(1, handler.HeadRequests);
        Assert.Equal(1, handler.RangeRequests);
        Assert.True(source.LeasesAreImmutable);
    }

    [Theory]
    [InlineData(GetEtagBehavior.Missing)]
    [InlineData(GetEtagBehavior.Changed)]
    [InlineData(GetEtagBehavior.Weak)]
    public async Task EveryPartialResponseMustRepeatTheOpenedStrongEtag(GetEtagBehavior behavior)
    {
        byte[] content = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        using var handler = new EquivalentEtagRangeHandler(content, behavior);
        using var client = new HttpClient(handler);
        await using HttpRangeSource source = await HttpRangeSource.OpenAsync(
            client,
            new Uri("https://assets.example.test/archive.bin"));

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            source.ReadExactlyAsync(3, new byte[7]).AsTask());

        Assert.Contains("ETag", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EquivalentEtagRangeHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly GetEtagBehavior _getEtagBehavior;
        private int _headRequests;
        private int _rangeRequests;

        internal EquivalentEtagRangeHandler(
            byte[] content,
            GetEtagBehavior getEtagBehavior = GetEtagBehavior.Same)
        {
            _content = content;
            _getEtagBehavior = getEtagBehavior;
        }

        internal int HeadRequests => Volatile.Read(ref _headRequests);
        internal int RangeRequests => Volatile.Read(ref _rangeRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref _headRequests);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([]),
                    RequestMessage = request,
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"stable-generation\"");
                response.Content.Headers.ContentLength = _content.LongLength;
                return Task.FromResult(response);
            }

            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException($"Unexpected HTTP method {request.Method}.");
            Interlocked.Increment(ref _rangeRequests);
            RangeItemHeaderValue requestedRange = Assert.Single(
                request.Headers.Range?.Ranges
                ?? throw new InvalidOperationException("Range request omitted the Range header."));
            long from = requestedRange.From
                ?? throw new InvalidOperationException("Range request omitted its start offset.");
            long to = requestedRange.To
                ?? throw new InvalidOperationException("Range request omitted its end offset.");
            EntityTagHeaderValue ifMatch = Assert.Single(request.Headers.IfMatch);
            Assert.Equal("\"stable-generation\"", ifMatch.Tag);
            byte[] slice = _content.AsSpan(
                checked((int)from),
                checked((int)(to - from + 1))).ToArray();
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
                RequestMessage = request,
            };
            // Same deliberately uses a distinct header object with the same validator value.
            partial.Headers.ETag = _getEtagBehavior switch
            {
                GetEtagBehavior.Same => new EntityTagHeaderValue("\"stable-generation\""),
                GetEtagBehavior.Changed => new EntityTagHeaderValue("\"changed-generation\""),
                GetEtagBehavior.Weak => new EntityTagHeaderValue("\"stable-generation\"", isWeak: true),
                GetEtagBehavior.Missing => null,
                _ => throw new InvalidOperationException(),
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _content.LongLength);
            return Task.FromResult(partial);
        }
    }

    public enum GetEtagBehavior
    {
        Same,
        Missing,
        Changed,
        Weak,
    }
}

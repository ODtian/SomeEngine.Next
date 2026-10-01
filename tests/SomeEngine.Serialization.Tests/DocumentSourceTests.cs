using System.Net;
using System.Net.Http.Headers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class DocumentSourceTests
{
    [Theory]
    [InlineData("file", ChunkCompression.None)]
    [InlineData("file", ChunkCompression.Brotli)]
    [InlineData("memory", ChunkCompression.None)]
    [InlineData("memory", ChunkCompression.Brotli)]
    [InlineData("mapping", ChunkCompression.None)]
    [InlineData("mapping", ChunkCompression.Brotli)]
    [InlineData("mutable", ChunkCompression.None)]
    [InlineData("mutable", ChunkCompression.Brotli)]
    [InlineData("http", ChunkCompression.None)]
    [InlineData("http", ChunkCompression.Brotli)]
    public async Task SourceKindDoesNotPreventDirectReadingOrScheduledDecoding(string sourceKind, ChunkCompression compression)
    {
        byte[] payload = new byte[512];
        new Random(42).NextBytes(payload);
        using MappedTestDocument encoded = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, payload, compression: compression).BuildMapped();
        using var client = new HttpClient(new DocumentHttpHandler(encoded));
        string? sourcePath = null;
        try
        {
            if (sourceKind is "file" or "mapping")
            {
                sourcePath = Path.Combine(Path.GetTempPath(), $"SomeEngine-source-comparison-{Guid.NewGuid():N}.bin");
                await File.WriteAllBytesAsync(sourcePath, encoded.Memory.ToArray());
            }
            IRangeSource source = sourceKind switch
            {
                "file" => FileRangeSource.Open(sourcePath!),
                "memory" => new MemoryRangeSource(encoded),
                "mapping" => MemoryMappedRangeSource.Open(sourcePath!),
                "mutable" => new MutableSource(encoded),
                "http" => await HttpRangeSource.OpenAsync(client, new Uri("https://assets.example.test/document.bin")),
                _ => throw new ArgumentException(nameof(sourceKind)),
            };
            await using var document = await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
            byte[] destination = new byte[payload.Length];
            int destinationRequests = 0;
            Memory<byte>? result = await document.TryReadChunkAsync(1, length =>
            {
                destinationRequests++;
                Assert.Equal(destination.Length, length);
                return destination;
            });
            Assert.Equal(1, destinationRequests);
            Assert.Equal(destination.AsMemory(), result);
            Assert.Equal(payload, destination);

            await document.DisposeAsync();
            Assert.Equal(payload, destination);
        }
        finally { if (sourcePath is not null) File.Delete(sourcePath); }
    }

    private sealed class MutableSource(ReadOnlyMemory<byte> memory) : IRangeSource
    {
        private readonly MemoryRangeSource _source = new(memory);
        public long Length => _source.Length;
        public string Generation => _source.Generation;
        public bool LeasesAreImmutable => false;
        public bool RetainsResidentBacking => true;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
            => _source.ReadExactlyAsync(offset, destination, cancellationToken);
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => _source.AcquireAsync(offset, length, cancellationToken);
        public ValueTask DisposeAsync() => _source.DisposeAsync();
    }

    private sealed class DocumentHttpHandler(ReadOnlyMemory<byte> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage { RequestMessage = request };
            response.Headers.ETag = new EntityTagHeaderValue("\"document-version\"");
            if (request.Method == HttpMethod.Head)
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = content.Length;
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
                long start = range.From!.Value;
                long end = range.To!.Value;
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent(content.Slice((int)start, (int)(end - start + 1)).ToArray());
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.Length);
            }
            return Task.FromResult(response);
        }
    }
}

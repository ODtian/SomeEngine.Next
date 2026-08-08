using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace SomeEngine.Serialization.IO;

/// <summary>HTTP byte-range source pinned to the ETag observed at open time.</summary>
public sealed class HttpRangeSource : IRangeSource
{
    private readonly HttpClient _client;
    private readonly Uri _uri;
    private readonly EntityTagHeaderValue _etag;
    private readonly bool _ownsClient;
    private int _disposed;

    private HttpRangeSource(
        HttpClient client,
        Uri uri,
        EntityTagHeaderValue etag,
        long length,
        bool ownsClient)
    {
        _client = client;
        _uri = uri;
        _etag = etag;
        _ownsClient = ownsClient;
        Length = length;
        Generation = $"http:{uri.AbsoluteUri}:{etag}:{length:X16}";
    }

    public long Length { get; }
    public string Generation { get; }
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => false;

    public static async ValueTask<HttpRangeSource> OpenAsync(
        HttpClient client,
        Uri uri,
        bool ownsClient = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EntityTagHeaderValue etag = response.Headers.ETag
            ?? throw new InvalidOperationException("HTTP range source requires a strong ETag.");
        if (etag.IsWeak)
            throw new InvalidOperationException("HTTP range source requires a strong ETag, not a weak validator.");
        long length = response.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("HTTP range source requires Content-Length.");
        if (length < 0)
            throw new InvalidOperationException("HTTP range source returned a negative Content-Length.");
        return new HttpRangeSource(client, uri, etag, length, ownsClient);
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, destination.Length, Length);
        if (destination.IsEmpty)
            return;

        long end = checked(offset + destination.Length - 1L);
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(offset, end);
        request.Headers.IfMatch.Add(_etag);
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new IOException("HTTP range source generation changed (If-Match failed).");
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException($"HTTP range request returned {(int)response.StatusCode} instead of 206.");
        EntityTagHeaderValue returnedEtag = response.Headers.ETag
            ?? throw new IOException("HTTP range response omitted the strong ETag that identifies the opened generation.");
        if (returnedEtag.IsWeak || !returnedEtag.Equals(_etag))
            throw new IOException("HTTP range response ETag differs from the opened source generation.");
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (range is null
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || range.From != offset
            || range.To != end
            || range.Length != Length)
            throw new IOException("HTTP Content-Range does not match the requested source range.");
        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength != destination.Length)
        {
            throw new IOException(
                $"HTTP range response declared {declaredLength} bytes for a {destination.Length}-byte range.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        int total = 0;
        while (total < destination.Length)
        {
            int read = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("HTTP range response was truncated.");
            total += read;
        }
        byte[] overflowProbe = new byte[1];
        if (await stream.ReadAsync(overflowProbe, cancellationToken).ConfigureAwait(false) != 0)
            throw new IOException("HTTP range response exceeded its declared length.");
    }

    public async ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, length, Length);
        if (length == 0)
            return RangeLease.Borrow(ReadOnlyMemory<byte>.Empty);

        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            await ReadExactlyAsync(offset, owner.Memory[..length], cancellationToken).ConfigureAwait(false);
            return RangeLease.Own(owner, length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsClient)
            _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

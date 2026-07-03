namespace Cairn.Client;

/// <summary>
/// A read-only pass-through over a response body that enforces the client's buffer cap, remembers a short
/// prefix for error snippets, and tracks whether any non-whitespace content was seen — so the body can
/// stream straight into <see cref="System.Text.Json.JsonDocument"/> without being buffered twice. Does not
/// own the inner stream; the response's disposal releases it.
/// </summary>
internal sealed class CappedBodyStream(Stream inner, long maxLength) : Stream
{
    private const int PrefixCapacity = 512;

    private byte[]? _prefix;
    private int _prefixLength;
    private long _length;

    /// <summary>Whether any non-whitespace byte has been read — an all-whitespace body is blank, not malformed JSON.</summary>
    public bool SawContent { get; private set; }

    /// <summary>The first bytes of the body, for diagnostics.</summary>
    public byte[] Prefix => _prefix is null ? [] : _prefix.AsSpan(0, _prefixLength).ToArray();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Track(ReadOnlySpan<byte> chunk)
    {
        _length += chunk.Length;
        if (_length > maxLength)
        {
            throw new HttpRequestException($"The response body exceeds the {maxLength:N0}-byte cap (HttpClient.MaxResponseContentBufferSize).");
        }

        if (_prefixLength < PrefixCapacity && chunk.Length > 0)
        {
            _prefix ??= new byte[PrefixCapacity];
            var take = Math.Min(chunk.Length, PrefixCapacity - _prefixLength);
            chunk[..take].CopyTo(_prefix.AsSpan(_prefixLength));
            _prefixLength += take;
        }

        if (!SawContent)
        {
            foreach (var b in chunk)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                {
                    SawContent = true;
                    break;
                }
            }
        }
    }
}

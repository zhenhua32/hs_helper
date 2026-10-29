using System.Runtime.CompilerServices;
using System.Text;

namespace HsHelper.Ingestion;

/// <summary>Reads complete UTF-8 lines up to the file length observed on open, including logs held by the game.</summary>
public static class SharedLogReplay
{
    public static async IAsyncEnumerable<string> ReadLinesAsync(string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var completeLength = await FindLastCompleteLineAsync(file, file.Length, cancellationToken);
        file.Position = 0;
        using var bounded = new BoundedReadStream(file, completeLength);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private static async Task<long> FindLastCompleteLineAsync(FileStream stream, long length, CancellationToken token)
    {
        var bytes = new byte[65536];
        var end = length;
        while (end > 0)
        {
            token.ThrowIfCancellationRequested();
            var start = Math.Max(0, end - bytes.Length);
            var count = (int)(end - start);
            stream.Position = start;
            await stream.ReadExactlyAsync(bytes.AsMemory(0, count), token);
            for (var i = count - 1; i >= 0; i--)
                if (bytes[i] == (byte)'\n') return start + i + 1;
            end = start;
        }
        return 0;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _length;
        private long _remaining;
        public BoundedReadStream(Stream source, long length)
        {
            _source = source;
            _length = length;
            _remaining = length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0) return 0;
            var read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (read == 0) throw new IOException("回放期间日志被截断，请重新载入。");
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_remaining == 0) return 0;
            var read = await _source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], token);
            if (read == 0) throw new IOException("回放期间日志被截断，请重新载入。");
            _remaining -= read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

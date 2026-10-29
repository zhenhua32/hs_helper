using System.Runtime.CompilerServices;
using System.Text;

namespace HsHelper.Ingestion;

public enum LogResetReason { None, InitialFile, SessionChanged, Truncated, Replaced }

public sealed record LogReadBatch(
    string? Path,
    LogResetReason ResetReason,
    IReadOnlyList<string> Lines,
    long ByteOffset,
    bool HasMoreData = false);

/// <summary>Incremental, UTF-8 line reader. Only complete lines are emitted; pending bytes survive reads.</summary>
public sealed class PowerLogTailer : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UTF8Encoding _utf8 = new(false, true);
    private readonly int _maxReadBytes;
    private string? _path;
    private long _offset;
    private byte[] _pending = [];
    private byte[] _anchor = [];
    private byte[] _checkpoint = [];
    private long _checkpointPosition;
    private DateTime _creationTime;
    private bool _firstLine;

    public PowerLogTailer(int maxReadBytes = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadBytes);
        _maxReadBytes = maxReadBytes;
    }

    public async Task<LogReadBatch> ReadAvailableAsync(string root, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var found = LogSessionDiscovery.FindLatestPowerLog(root);
            if (found is null)
                return new(null, LogResetReason.None, Array.Empty<string>(), _offset);
            var reason = LogResetReason.None;
            using var stream = new FileStream(found.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var creationTime = File.GetCreationTimeUtc(found.Path);
            if (_path is null) reason = LogResetReason.InitialFile;
            else if (!_path.Equals(found.Path, StringComparison.OrdinalIgnoreCase)) reason = LogResetReason.SessionChanged;
            else if (stream.Length < _offset) reason = LogResetReason.Truncated;
            else if (creationTime != _creationTime || !await HasSameContentAsync(stream, cancellationToken)) reason = LogResetReason.Replaced;

            if (reason != LogResetReason.None)
            {
                _path = found.Path;
                _offset = 0;
                _pending = [];
                _anchor = [];
                _checkpoint = [];
                _checkpointPosition = 0;
                _firstLine = true;
                _creationTime = creationTime;
            }
            stream.Position = _offset;
            var remaining = stream.Length - _offset;
            var bytes = new byte[(int)Math.Min(remaining, _maxReadBytes)];
            var read = 0;
            while (read < bytes.Length)
            {
                var amount = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken);
                if (amount == 0) break;
                read += amount;
            }
            var combined = new byte[_pending.Length + read];
            _pending.CopyTo(combined, 0);
            bytes.AsSpan(0, read).CopyTo(combined.AsSpan(_pending.Length));
            var lines = new List<string>();
            var start = 0;
            var firstLine = _firstLine;
            for (var i = 0; i < combined.Length; i++)
            {
                if (combined[i] != (byte)'\n') continue;
                var end = i;
                if (end > start && combined[end - 1] == (byte)'\r') end--;
                var line = _utf8.GetString(combined, start, end - start);
                if (firstLine) line = line.TrimStart('\uFEFF');
                firstLine = false;
                lines.Add(line);
                start = i + 1;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (combined.Length - start > 4 * 1024 * 1024)
                throw new InvalidDataException("日志单行超过四 MiB，无法可靠读取。");
            var newOffset = _offset + read;
            var anchor = _anchor;
            if (anchor.Length == 0 && newOffset > 0)
            {
                stream.Position = 0;
                anchor = new byte[(int)Math.Min(newOffset, 64)];
                await stream.ReadExactlyAsync(anchor, cancellationToken);
            }
            var checkpoint = Array.Empty<byte>();
            var checkpointPosition = 0L;
            if (newOffset > 0)
            {
                checkpoint = new byte[(int)Math.Min(newOffset, 64)];
                checkpointPosition = newOffset - checkpoint.Length;
                stream.Position = checkpointPosition;
                await stream.ReadExactlyAsync(checkpoint, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Commit byte position and decoder state together, including when a caller cancels a read.
            _offset = newOffset;
            _pending = combined[start..];
            _firstLine = firstLine;
            _anchor = anchor;
            _checkpoint = checkpoint;
            _checkpointPosition = checkpointPosition;
            return new(found.Path, reason, lines.AsReadOnly(), _offset, stream.Length > _offset);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<LogReadBatch> WatchAsync(string root, TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        while (!cancellationToken.IsCancellationRequested)
        {
            LogReadBatch batch;
            try
            {
                batch = await ReadAvailableAsync(root, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A session can be rotated between discovery and opening its file.
                await Task.Delay(interval, cancellationToken);
                continue;
            }
            if (batch.ResetReason != LogResetReason.None || batch.Lines.Count > 0)
                yield return batch;
            if (!batch.HasMoreData) await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task<bool> HasSameContentAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (_anchor.Length == 0) return true;
        if (stream.Length < _anchor.Length) return false;
        stream.Position = 0;
        var current = new byte[_anchor.Length];
        await stream.ReadExactlyAsync(current, cancellationToken);
        if (!current.AsSpan().SequenceEqual(_anchor)) return false;
        if (_checkpoint.Length == 0) return true;
        stream.Position = _checkpointPosition;
        var checkpoint = new byte[_checkpoint.Length];
        await stream.ReadExactlyAsync(checkpoint, cancellationToken);
        return checkpoint.AsSpan().SequenceEqual(_checkpoint);
    }

    public void Dispose() => _gate.Dispose();
}

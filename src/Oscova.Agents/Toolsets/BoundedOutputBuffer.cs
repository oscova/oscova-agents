using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Captures combined shell output without letting a long-running command exhaust memory.
/// </summary>
public sealed class BoundedOutputBuffer
{
    /// <summary>The default retained output size per job: 1 MiB.</summary>
    public const int DefaultCapacityBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly int _capacityBytes;
    private readonly int _headCapacityBytes;
    private readonly int _tailCapacityBytes;
    private readonly List<byte> _head;
    private readonly List<byte> _tail;

    private bool _truncated;
    private long _tailStartOffset;
    private long _totalSize;

    /// <summary>
    /// Creates a bounded output buffer with the supplied byte capacity.
    /// </summary>
    public BoundedOutputBuffer(int capacityBytes = DefaultCapacityBytes)
    {
        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Capacity must be positive.");
        }

        _capacityBytes = capacityBytes;
        _headCapacityBytes = Math.Max(1, capacityBytes / 2);
        _tailCapacityBytes = capacityBytes - _headCapacityBytes;
        _head = new List<byte>(_headCapacityBytes);
        _tail = new List<byte>(Math.Max(1, _tailCapacityBytes));
    }

    /// <summary>
    /// Gets the logical byte length of everything appended to the buffer.
    /// </summary>
    public long TotalSize
    {
        get
        {
            lock (_gate)
            {
                return _totalSize;
            }
        }
    }

    /// <summary>
    /// True once output exceeded the retained capacity and middle bytes were dropped.
    /// </summary>
    public bool Truncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>
    /// Appends bytes to the logical output stream.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            long previousTotalSize = _totalSize;
            _totalSize += data.Length;

            if (!_truncated && _head.Count + data.Length <= _capacityBytes)
            {
                AppendBytes(_head, data);
                return;
            }

            if (!_truncated)
            {
                StartTruncating(data);
                return;
            }

            AppendTail(data);
            _tailStartOffset = _totalSize - _tail.Count;
            _truncated = _tailStartOffset > _head.Count || previousTotalSize > _capacityBytes;
        }
    }

    /// <summary>Records unavailable bytes in the logical output stream.</summary>
    public void AppendGap(long byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Gap length cannot be negative.");
        }

        if (byteCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_head.Count > _headCapacityBytes)
            {
                _head.RemoveRange(_headCapacityBytes, _head.Count - _headCapacityBytes);
            }

            _tail.Clear();
            _totalSize += byteCount;
            _tailStartOffset = _totalSize;
            _truncated = true;
        }
    }

    /// <summary>
    /// Reads retained output after an absolute logical stream offset.
    /// </summary>
    public (string Text, long NewOffset, long TotalSize, bool Truncated) ReadFrom(long offset, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            lock (_gate)
            {
                long normalizedOffset = Math.Clamp(offset, 0, _totalSize);
                return (string.Empty, normalizedOffset, _totalSize, _truncated);
            }
        }

        lock (_gate)
        {
            long currentOffset = Math.Clamp(offset, 0, _totalSize);
            if (currentOffset >= _totalSize)
            {
                return (string.Empty, _totalSize, _totalSize, _truncated);
            }

            List<byte> output = new(Math.Min(maxBytes, _capacityBytes));

            if (!_truncated)
            {
                currentOffset = CopyFromRange(_head, 0, currentOffset, maxBytes, output);
                return (Encoding.UTF8.GetString(output.ToArray()), currentOffset, _totalSize, _truncated);
            }

            if (currentOffset < _head.Count)
            {
                currentOffset = CopyFromRange(_head, 0, currentOffset, maxBytes, output);
            }

            if (output.Count < maxBytes)
            {
                if (currentOffset < _tailStartOffset)
                {
                    currentOffset = _tailStartOffset;
                }

                if (currentOffset < _totalSize)
                {
                    currentOffset = CopyFromRange(_tail, _tailStartOffset, currentOffset, maxBytes - output.Count, output);
                }
            }

            return (Encoding.UTF8.GetString(output.ToArray()), currentOffset, _totalSize, _truncated);
        }
    }

    private void StartTruncating(ReadOnlySpan<byte> data)
    {
        byte[] previousBytes = _head.ToArray();
        _head.Clear();
        _tail.Clear();

        CopyFirstBytes(previousBytes, data, _headCapacityBytes, _head);
        CopyLastBytes(previousBytes, data, _tailCapacityBytes, _tail);

        _tailStartOffset = _totalSize - _tail.Count;
        _truncated = _tailStartOffset > _head.Count;
    }

    private void AppendTail(ReadOnlySpan<byte> data)
    {
        if (_tailCapacityBytes == 0)
        {
            _tail.Clear();
            return;
        }

        if (data.Length >= _tailCapacityBytes)
        {
            _tail.Clear();
            AppendBytes(_tail, data[^_tailCapacityBytes..]);
            return;
        }

        AppendBytes(_tail, data);
        int extraBytes = _tail.Count - _tailCapacityBytes;
        if (extraBytes > 0)
        {
            _tail.RemoveRange(0, extraBytes);
        }
    }

    private static void CopyFirstBytes(byte[] previousBytes, ReadOnlySpan<byte> newBytes, int count, List<byte> destination)
    {
        int fromPrevious = Math.Min(count, previousBytes.Length);
        AppendBytes(destination, previousBytes.AsSpan(0, fromPrevious));

        int remaining = count - fromPrevious;
        if (remaining > 0)
        {
            AppendBytes(destination, newBytes[..Math.Min(remaining, newBytes.Length)]);
        }
    }

    private static void CopyLastBytes(byte[] previousBytes, ReadOnlySpan<byte> newBytes, int count, List<byte> destination)
    {
        if (count <= 0)
        {
            return;
        }

        if (newBytes.Length >= count)
        {
            AppendBytes(destination, newBytes[^count..]);
            return;
        }

        int fromPrevious = Math.Min(count - newBytes.Length, previousBytes.Length);
        if (fromPrevious > 0)
        {
            AppendBytes(destination, previousBytes.AsSpan(previousBytes.Length - fromPrevious, fromPrevious));
        }

        AppendBytes(destination, newBytes);
    }

    private static long CopyFromRange(
        List<byte> source,
        long sourceStartOffset,
        long requestedOffset,
        int maxBytes,
        List<byte> destination)
    {
        long sourceEndOffset = sourceStartOffset + source.Count;
        if (requestedOffset >= sourceEndOffset || maxBytes <= 0)
        {
            return requestedOffset;
        }

        int sourceIndex = (int)Math.Max(0, requestedOffset - sourceStartOffset);
        int bytesToCopy = Math.Min(maxBytes, source.Count - sourceIndex);
        if (bytesToCopy <= 0)
        {
            return requestedOffset;
        }

        destination.AddRange(source.GetRange(sourceIndex, bytesToCopy));
        return requestedOffset + bytesToCopy;
    }

    private static void AppendBytes(List<byte> destination, ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            destination.Add(bytes[index]);
        }
    }
}

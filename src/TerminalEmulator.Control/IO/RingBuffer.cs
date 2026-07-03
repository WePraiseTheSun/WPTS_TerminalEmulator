using System;

namespace TerminalEmulator.Control.IO
{
    /// <summary>
    /// A fixed-capacity, thread-safe circular byte buffer. Producers never
    /// block: when the buffer is full the oldest data is discarded and a
    /// dropped-byte counter is incremented so the consumer can surface the
    /// truncation to the user instead of silently corrupting the stream.
    /// </summary>
    public sealed class RingBuffer
    {
        private readonly byte[] _buffer;
        private readonly object _sync = new object();
        private int _head;   // next read position
        private int _tail;   // next write position
        private int _count;
        private long _dropped;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new byte[capacity];
        }

        public int Count
        {
            get { lock (_sync) { return _count; } }
        }

        public int Capacity
        {
            get { return _buffer.Length; }
        }

        public void Write(byte[] source, int offset, int length)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (length <= 0) return;

            lock (_sync)
            {
                // Incoming chunk larger than the whole buffer: keep only its tail.
                if (length >= _buffer.Length)
                {
                    _dropped += _count + (length - _buffer.Length);
                    offset += length - _buffer.Length;
                    length = _buffer.Length;
                    _head = 0;
                    _tail = 0;
                    _count = 0;
                }

                int overflow = _count + length - _buffer.Length;
                if (overflow > 0)
                {
                    _head = (_head + overflow) % _buffer.Length;
                    _count -= overflow;
                    _dropped += overflow;
                }

                int firstSegment = Math.Min(length, _buffer.Length - _tail);
                Buffer.BlockCopy(source, offset, _buffer, _tail, firstSegment);

                int remaining = length - firstSegment;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(source, offset + firstSegment, _buffer, 0, remaining);
                }

                _tail = (_tail + length) % _buffer.Length;
                _count += length;
            }
        }

        /// <summary>
        /// Reads up to <paramref name="maxBytes"/> into <paramref name="destination"/>.
        /// Returns the number of bytes read.
        /// </summary>
        public int Read(byte[] destination, int maxBytes)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            lock (_sync)
            {
                int toRead = Math.Min(Math.Min(maxBytes, _count), destination.Length);
                if (toRead <= 0) return 0;

                int firstSegment = Math.Min(toRead, _buffer.Length - _head);
                Buffer.BlockCopy(_buffer, _head, destination, 0, firstSegment);

                int remaining = toRead - firstSegment;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_buffer, 0, destination, firstSegment, remaining);
                }

                _head = (_head + toRead) % _buffer.Length;
                _count -= toRead;
                return toRead;
            }
        }

        /// <summary>
        /// Returns the number of bytes dropped since the last call and resets
        /// the counter.
        /// </summary>
        public long TakeDroppedCount()
        {
            lock (_sync)
            {
                long dropped = _dropped;
                _dropped = 0;
                return dropped;
            }
        }
    }
}

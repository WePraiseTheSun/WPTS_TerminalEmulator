using System;
using System.Text;
using System.Threading;

namespace TerminalEmulator.Control.IO
{
    /// <summary>
    /// Buffers raw PTY output and flushes it as decoded UTF-8 strings using an
    /// adaptive strategy:
    ///
    ///  * Interactive mode — flush within a few milliseconds of data arriving.
    ///    Active for a short window after each user keystroke so echo latency
    ///    stays imperceptible.
    ///  * Batch mode — aggregate output into ~30 ms windows. Active during
    ///    bulk output (builds, logs, git) to keep WebView2 IPC message counts
    ///    and DOM churn low.
    ///
    /// A stateful UTF-8 decoder is used so multi-byte sequences split across
    /// pipe reads are reassembled correctly. If the ring buffer overflows the
    /// decoder is reset and a visible truncation notice is injected into the
    /// stream rather than emitting corrupt escape sequences.
    /// </summary>
    public sealed class OutputAggregator : IDisposable
    {
        private const int InteractiveWindowMs = 250;
        private const int InteractiveFlushDelayMs = 4;
        private const int BatchFlushDelayMs = 30;
        private const int ForceFlushThresholdBytes = 64 * 1024;
        private const int MaxChunkBytes = 128 * 1024;

        private readonly RingBuffer _ring;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly Timer _timer;
        private readonly byte[] _drainBuffer = new byte[MaxChunkBytes];
        private readonly char[] _charBuffer;
        private readonly object _pumpSync = new object();

        private int _pumpScheduled;          // 0/1 via Interlocked
        private long _lastInputTicks;
        private volatile bool _disposed;

        /// <summary>Raised (on a thread-pool thread) with a decoded chunk of output.</summary>
        public event Action<string> Flush;

        public OutputAggregator(int ringCapacityBytes = 1024 * 1024)
        {
            _ring = new RingBuffer(ringCapacityBytes);
            _charBuffer = new char[Encoding.UTF8.GetMaxCharCount(MaxChunkBytes)];
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Marks recent user input so the flush strategy switches to low-latency mode.</summary>
        public void NotifyUserInput()
        {
            Interlocked.Exchange(ref _lastInputTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Resets decoder state (used across shell restarts).</summary>
        public void Reset()
        {
            lock (_pumpSync)
            {
                _decoder.Reset();
            }
        }

        public void Post(byte[] data, int offset, int length)
        {
            if (_disposed || length <= 0) return;

            _ring.Write(data, offset, length);

            if (_ring.Count >= ForceFlushThresholdBytes)
            {
                SchedulePump(0);
            }
            else
            {
                SchedulePump(IsInteractive() ? InteractiveFlushDelayMs : BatchFlushDelayMs);
            }
        }

        private bool IsInteractive()
        {
            long last = Interlocked.Read(ref _lastInputTicks);
            return (DateTime.UtcNow.Ticks - last) < TimeSpan.FromMilliseconds(InteractiveWindowMs).Ticks;
        }

        private void SchedulePump(int delayMs)
        {
            if (_disposed) return;

            if (Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) == 0)
            {
                try
                {
                    _timer.Change(delayMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void OnTimer(object state)
        {
            Interlocked.Exchange(ref _pumpScheduled, 0);
            Pump();

            // Data may have arrived between the drain and the flag reset.
            if (!_disposed && _ring.Count > 0)
            {
                SchedulePump(IsInteractive() ? InteractiveFlushDelayMs : BatchFlushDelayMs);
            }
        }

        private void Pump()
        {
            if (_disposed) return;

            var handler = Flush;

            lock (_pumpSync)
            {
                long dropped = _ring.TakeDroppedCount();
                if (dropped > 0)
                {
                    // Mid-stream bytes were discarded; escape-sequence state is
                    // unrecoverable, so reset decoding and tell the user.
                    _decoder.Reset();
                    if (handler != null)
                    {
                        handler("\r\n\u001b[33m[terminal] output overflow: " + dropped +
                                " bytes dropped\u001b[0m\r\n");
                    }
                }

                while (true)
                {
                    int bytesRead = _ring.Read(_drainBuffer, MaxChunkBytes);
                    if (bytesRead <= 0) break;

                    int chars = _decoder.GetChars(_drainBuffer, 0, bytesRead, _charBuffer, 0, false);
                    if (chars > 0 && handler != null)
                    {
                        handler(new string(_charBuffer, 0, chars));
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}

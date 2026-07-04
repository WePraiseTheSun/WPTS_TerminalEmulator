using System;
using System.Text;

namespace TerminalEmulator.Control.Sessions
{
    /// <summary>
    /// Tracks the shell's current working directory from shell-integration
    /// escape sequences in the output stream.
    ///
    /// Recognizes <c>OSC 9;9;&lt;path&gt;</c> (the ConEmu/Windows Terminal
    /// working-directory report), terminated by BEL (0x07) or ST (ESC \).
    /// The path may optionally be double-quoted. This is the only reliable
    /// CWD source for shells like PowerShell that track their location
    /// without changing the Win32 process working directory.
    ///
    /// The scanner is incremental: sequences split across pipe reads or
    /// aggregator flushes are reassembled via a small rolling tail buffer.
    /// Thread-safe for one writer (the aggregator flush) and many readers.
    /// </summary>
    internal sealed class WorkingDirectoryTracker
    {
        private const string Prefix = "\u001b]9;9;";
        private const int MaxPathLength = 1024;     // sanity cap for a report
        private const int TailCapacity = 1300;      // must exceed Prefix + MaxPathLength + terminator

        private readonly StringBuilder _tail = new StringBuilder(TailCapacity + 64);
        private readonly object _sync = new object();
        private volatile string _currentDirectory;

        /// <summary>Latest reported directory, or null when nothing was reported yet.</summary>
        public string CurrentDirectory
        {
            get { return _currentDirectory; }
        }

        /// <summary>Feeds a chunk of decoded terminal output to the scanner.</summary>
        public void Scan(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;

            lock (_sync)
            {
                _tail.Append(chunk);

                int searchFrom = 0;
                while (true)
                {
                    int start = IndexOf(_tail, Prefix, searchFrom);
                    if (start < 0) break;

                    int payloadStart = start + Prefix.Length;
                    int end = -1;       // index just past the terminator
                    int payloadEnd = -1;

                    for (int i = payloadStart; i < _tail.Length; i++)
                    {
                        char c = _tail[i];
                        if (c == '\u0007')                                   // BEL
                        {
                            payloadEnd = i;
                            end = i + 1;
                            break;
                        }
                        if (c == '\u001b')                                   // ESC \ (ST)
                        {
                            if (i + 1 >= _tail.Length) break;                // split; wait for more
                            if (_tail[i + 1] == '\\')
                            {
                                payloadEnd = i;
                                end = i + 2;
                            }
                            else
                            {
                                payloadEnd = -2;                             // malformed: abort this one
                            }
                            break;
                        }
                        if (i - payloadStart > MaxPathLength) { payloadEnd = -2; break; }
                    }

                    if (payloadEnd == -2)
                    {
                        // Malformed / oversized: skip this prefix occurrence.
                        searchFrom = payloadStart;
                        continue;
                    }
                    if (end < 0)
                    {
                        // Incomplete sequence: keep the tail from the prefix on
                        // and wait for the next chunk.
                        if (start > 0) _tail.Remove(0, start);
                        return;
                    }

                    string path = _tail.ToString(payloadStart, payloadEnd - payloadStart).Trim();
                    if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                    {
                        path = path.Substring(1, path.Length - 2);
                    }
                    if (path.Length > 0)
                    {
                        _currentDirectory = path;
                    }

                    searchFrom = end;
                }

                // No pending partial sequence: a split can only be a partial
                // prefix at the very end, so keeping the last few characters
                // is always sufficient.
                int keep = Prefix.Length - 1;
                if (_tail.Length > keep)
                {
                    _tail.Remove(0, _tail.Length - keep);
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _tail.Length = 0;
                _currentDirectory = null;
            }
        }

        private static int IndexOf(StringBuilder builder, string value, int startIndex)
        {
            int limit = builder.Length - value.Length;
            for (int i = Math.Max(0, startIndex); i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < value.Length; j++)
                {
                    if (builder[i + j] != value[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}

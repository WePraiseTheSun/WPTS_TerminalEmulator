using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TerminalEmulator.Control.IO;
using TerminalEmulator.Control.Native;

namespace TerminalEmulator.Control.Sessions
{
    /// <summary>
    /// One terminal session: an isolated ConPTY instance, its child process
    /// tree, a ring-buffered output pipeline and a scrollback replay cache.
    ///
    /// Sessions can be detached (background tab) and re-attached; while
    /// detached, output continues to accumulate in the replay cache so the
    /// renderer can be repopulated instantly on switch.
    ///
    /// Renderer backpressure: the control credits back rendered characters via
    /// <see cref="AcknowledgeRendered"/>. When the un-acknowledged volume
    /// exceeds a high-water mark the pipe read loop pauses, which naturally
    /// propagates backpressure to the shell through the OS pipe.
    /// </summary>
    public sealed class TerminalSession : IDisposable
    {
        private const int HighWaterMarkChars = 512 * 1024;
        private const int ReplayCapacityChars = 2 * 1024 * 1024;
        private const int RestartDelayMs = 750;

        public string Id { get; }
        public ShellProfile Profile { get; }
        public bool IsAttached { get { return _attached; } }
        public int Columns { get { return _cols; } }
        public int Rows { get { return _rows; } }

        public bool IsRunning
        {
            get
            {
                var process = _process;
                return process != null && !process.HasExited;
            }
        }

        /// <summary>Process id of the root shell process (0 when not running).</summary>
        public int ProcessId
        {
            get
            {
                var process = _process;
                return process != null ? process.ProcessId : 0;
            }
        }

        /// <summary>
        /// Best-effort live working directory of the shell, read from the
        /// child process (accurate for cmd; PowerShell reports its launch
        /// directory because it tracks location without changing the process
        /// CWD). Falls back to the profile's start directory.
        /// </summary>
        public string GetWorkingDirectory()
        {
            var process = _process;
            if (process != null && !process.HasExited)
            {
                string cwd = ProcessWorkingDirectory.TryGet(process.ProcessId);
                if (!string.IsNullOrEmpty(cwd)) return cwd;
            }
            return Profile.StartDirectory;
        }

        /// <summary>Decoded output ready for the renderer. Raised on thread-pool threads.</summary>
        public event Action<TerminalSession, string> Output;

        /// <summary>The shell process exited. Arguments: session, exit code, will-restart.</summary>
        public event Action<TerminalSession, int, bool> Exited;

        private readonly OutputAggregator _aggregator;
        private readonly StringBuilder _replay = new StringBuilder();
        private readonly object _replaySync = new object();
        private readonly object _writeSync = new object();
        private readonly object _lifecycleSync = new object();

        private PseudoConsole _console;
        private ConPtyProcess _process;
        private FileStream _inputWriter;
        private FileStream _outputReader;
        private CancellationTokenSource _cts;

        private volatile bool _attached;
        private volatile bool _disposed;
        private long _outstandingChars;
        private int _cols;
        private int _rows;
        private int _restartCount;

        public TerminalSession(ShellProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            _aggregator = new OutputAggregator();
            _aggregator.Flush += HandleDecodedOutput;
        }

        // -------------------------------------------------------------------
        // Lifecycle
        // -------------------------------------------------------------------

        public void Start(int cols, int rows)
        {
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TerminalSession));
                if (_process != null) throw new InvalidOperationException("Session already started.");

                StartCore(cols, rows);
            }
        }

        private void StartCore(int cols, int rows)
        {
            _cols = cols > 0 ? cols : 120;
            _rows = rows > 0 ? rows : 30;
            _cts = new CancellationTokenSource();
            _aggregator.Reset();

            PseudoConsolePipe inputPipe = null;
            PseudoConsolePipe outputPipe = null;

            try
            {
                inputPipe = new PseudoConsolePipe();   // host writes -> console reads
                outputPipe = new PseudoConsolePipe();  // console writes -> host reads

                _console = PseudoConsole.Create(inputPipe.ReadSide, outputPipe.WriteSide, _cols, _rows);
                _process = ConPtyProcess.Start(Profile.CommandLine, Profile.StartDirectory, _console.Handle);

                // ConPTY duplicates the handles it needs. Closing our copies of
                // the console-side ends lets the read loop observe EOF cleanly.
                inputPipe.ReadSide.Dispose();
                outputPipe.WriteSide.Dispose();

                _inputWriter = new FileStream(inputPipe.WriteSide, FileAccess.Write);
                _outputReader = new FileStream(outputPipe.ReadSide, FileAccess.Read);

                var token = _cts.Token;
                var reader = _outputReader;
                var process = _process;

                Task.Run(() => ReadLoop(reader, token));
                Task.Run(() => MonitorExitAsync(process, token));
            }
            catch
            {
                if (inputPipe != null) inputPipe.Dispose();
                if (outputPipe != null) outputPipe.Dispose();
                TearDownCore();
                throw;
            }
        }

        private void ReadLoop(FileStream reader, CancellationToken token)
        {
            var buffer = new byte[8192];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Backpressure: while the renderer is behind, stop reading.
                    // The OS pipe fills and the shell blocks on write, exactly
                    // like a real terminal that can't keep up.
                    while (_attached &&
                           Interlocked.Read(ref _outstandingChars) > HighWaterMarkChars &&
                           !token.IsCancellationRequested)
                    {
                        Thread.Sleep(5);
                    }

                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break; // EOF: pseudo console closed

                    _aggregator.Post(buffer, 0, read);
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }

        private async Task MonitorExitAsync(ConPtyProcess process, CancellationToken token)
        {
            int exitCode;
            try
            {
                exitCode = await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || token.IsCancellationRequested) return;

            bool willRestart = Profile.AutoRestart && _restartCount < Profile.MaxAutoRestarts;

            HandleDecodedOutput(
                "\r\n\u001b[90m[process exited with code " + exitCode +
                (willRestart ? " — restarting shell]" : "]") + "\u001b[0m\r\n");

            var exited = Exited;
            if (exited != null) exited(this, exitCode, willRestart);

            if (!willRestart) return;

            await Task.Delay(RestartDelayMs).ConfigureAwait(false);
            if (_disposed) return;

            lock (_lifecycleSync)
            {
                if (_disposed) return;
                _restartCount++;
                TearDownCore();
                try
                {
                    StartCore(_cols, _rows);
                }
                catch (Exception ex)
                {
                    HandleDecodedOutput("\r\n\u001b[31m[restart failed: " + ex.Message + "]\u001b[0m\r\n");
                }
            }
        }

        private void TearDownCore()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            // Order matters: closing the pseudo console detaches conhost and
            // unblocks the read loop; only then terminate a lingering process
            // and release the pipe streams.
            if (_console != null)
            {
                _console.Dispose();
                _console = null;
            }

            if (_process != null)
            {
                _process.Kill();
                _process.Dispose();
                _process = null;
            }

            if (_inputWriter != null)
            {
                try { _inputWriter.Dispose(); } catch (IOException) { }
                _inputWriter = null;
            }

            if (_outputReader != null)
            {
                try { _outputReader.Dispose(); } catch (IOException) { }
                _outputReader = null;
            }
        }

        // -------------------------------------------------------------------
        // I/O
        // -------------------------------------------------------------------

        public void WriteInput(string text)
        {
            if (string.IsNullOrEmpty(text) || _disposed) return;

            var writer = _inputWriter;
            if (writer == null) return;

            _aggregator.NotifyUserInput();

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (_writeSync)
            {
                try
                {
                    writer.Write(bytes, 0, bytes.Length);
                    writer.Flush();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Pastes text, normalizing line endings to carriage returns for the PTY.</summary>
        public void Paste(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            WriteInput(text.Replace("\r\n", "\r").Replace("\n", "\r"));
        }

        public void Resize(int cols, int rows)
        {
            if (_disposed || cols <= 0 || rows <= 0) return;
            if (cols == _cols && rows == _rows) return;

            _cols = cols;
            _rows = rows;

            var console = _console;
            if (console != null)
            {
                try { console.Resize(cols, rows); }
                catch (ObjectDisposedException) { }
            }
        }

        private void HandleDecodedOutput(string payload)
        {
            if (_disposed || string.IsNullOrEmpty(payload)) return;

            lock (_replaySync)
            {
                _replay.Append(payload);
                if (_replay.Length > ReplayCapacityChars)
                {
                    _replay.Remove(0, _replay.Length - ReplayCapacityChars);
                }
            }

            if (_attached)
            {
                Interlocked.Add(ref _outstandingChars, payload.Length);
                var output = Output;
                if (output != null) output(this, payload);
            }
        }

        // -------------------------------------------------------------------
        // Attach / detach / backpressure
        // -------------------------------------------------------------------

        /// <summary>
        /// Attaches the renderer stream and returns the cached scrollback so
        /// the front end can repopulate instantly.
        /// </summary>
        public string Attach()
        {
            Interlocked.Exchange(ref _outstandingChars, 0);
            string replay;
            lock (_replaySync)
            {
                replay = _replay.ToString();
            }
            _attached = true;
            return replay;
        }

        public void Detach()
        {
            _attached = false;
            Interlocked.Exchange(ref _outstandingChars, 0);
        }

        /// <summary>Credits characters the renderer has finished writing.</summary>
        public void AcknowledgeRendered(long chars)
        {
            if (chars <= 0) return;
            long value = Interlocked.Add(ref _outstandingChars, -chars);
            if (value < 0) Interlocked.Exchange(ref _outstandingChars, 0);
        }

        /// <summary>
        /// Drops the scrollback replay cache. Called when the renderer clears
        /// its screen (Ctrl+L) so a later detach/attach doesn't resurrect the
        /// content the user explicitly cleared.
        /// </summary>
        public void ClearReplay()
        {
            lock (_replaySync)
            {
                _replay.Clear();
            }
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                _disposed = true;
                _attached = false;

                TearDownCore();
                _aggregator.Dispose();
            }
        }
    }
}

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerminalEmulator.Control.Native
{
    /// <summary>
    /// Owns a Windows pseudo console (HPCON) handle and manages its lifetime.
    /// </summary>
    internal sealed class PseudoConsole : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;
        private readonly object _sync = new object();

        public IntPtr Handle
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PseudoConsole));
                    return _handle;
                }
            }
        }

        private PseudoConsole(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// Creates a pseudo console bound to the given pipe ends.
        /// <paramref name="input"/> is the end the console *reads* from (the host
        /// writes user keystrokes into the other end); <paramref name="output"/>
        /// is the end the console *writes* to (the host reads rendered VT output
        /// from the other end).
        /// </summary>
        public static PseudoConsole Create(SafeFileHandle input, SafeFileHandle output, int cols, int rows)
        {
            if (cols <= 0) cols = 80;
            if (rows <= 0) rows = 25;

            int hr = NativeApi.CreatePseudoConsole(
                new NativeApi.COORD(cols, rows), input, output, 0, out var hPC);

            if (hr != NativeApi.S_OK)
            {
                throw new Win32Exception(hr,
                    "CreatePseudoConsole failed. ConPTY requires Windows 10 1809 (build 17763) or later.");
            }

            return new PseudoConsole(hPC);
        }

        public void Resize(int cols, int rows)
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (cols <= 0 || rows <= 0) return;

                int hr = NativeApi.ResizePseudoConsole(_handle, new NativeApi.COORD(cols, rows));
                if (hr != NativeApi.S_OK)
                {
                    throw new Win32Exception(hr, "ResizePseudoConsole failed.");
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;

                if (_handle != IntPtr.Zero)
                {
                    // Closing the pseudo console signals attached clients and
                    // releases the conhost-side resources.
                    NativeApi.ClosePseudoConsole(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }
}

using System;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace TerminalEmulator.Control.Native
{
    /// <summary>
    /// An anonymous pipe pair. One side is handed to the pseudo console, the
    /// other side is retained by the host for streaming.
    /// </summary>
    internal sealed class PseudoConsolePipe : IDisposable
    {
        public SafeFileHandle ReadSide { get; }
        public SafeFileHandle WriteSide { get; }

        public PseudoConsolePipe()
        {
            if (!NativeApi.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            {
                throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "Failed to create anonymous pipe for pseudo console.");
            }

            ReadSide = read;
            WriteSide = write;
        }

        public void Dispose()
        {
            if (!ReadSide.IsClosed) ReadSide.Dispose();
            if (!WriteSide.IsClosed) WriteSide.Dispose();
        }
    }
}

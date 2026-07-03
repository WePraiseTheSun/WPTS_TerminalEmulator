using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TerminalEmulator.Control.Native
{
    /// <summary>
    /// A child process created with EXTENDED_STARTUPINFO_PRESENT and bound to a
    /// pseudo console through PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE. Owns the
    /// process/thread handles and the attribute list memory.
    /// </summary>
    internal sealed class ConPtyProcess : IDisposable
    {
        private NativeApi.PROCESS_INFORMATION _processInfo;
        private IntPtr _attributeList = IntPtr.Zero;
        private bool _disposed;
        private readonly object _sync = new object();

        public int ProcessId
        {
            get { return _processInfo.dwProcessId; }
        }

        public bool HasExited
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed || _processInfo.hProcess == IntPtr.Zero) return true;
                    return NativeApi.WaitForSingleObject(_processInfo.hProcess, 0) == NativeApi.WAIT_OBJECT_0;
                }
            }
        }

        private ConPtyProcess()
        {
        }

        public static ConPtyProcess Start(string commandLine, string workingDirectory, IntPtr pseudoConsoleHandle)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                throw new ArgumentException("A command line is required.", nameof(commandLine));

            var process = new ConPtyProcess();

            var startupInfo = new NativeApi.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf(typeof(NativeApi.STARTUPINFOEX));

            // Size probe, then allocate and initialize the attribute list.
            var listSize = IntPtr.Zero;
            NativeApi.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);

            process._attributeList = Marshal.AllocHGlobal(listSize);

            try
            {
                if (!NativeApi.InitializeProcThreadAttributeList(process._attributeList, 1, 0, ref listSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "InitializeProcThreadAttributeList failed.");
                }

                if (!NativeApi.UpdateProcThreadAttribute(
                        process._attributeList,
                        0,
                        NativeApi.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        pseudoConsoleHandle,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "UpdateProcThreadAttribute(PSEUDOCONSOLE) failed.");
                }

                startupInfo.lpAttributeList = process._attributeList;

                string cwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

                if (!NativeApi.CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        NativeApi.EXTENDED_STARTUPINFO_PRESENT,
                        IntPtr.Zero,
                        cwd,
                        ref startupInfo,
                        out process._processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "CreateProcess failed for command line: " + commandLine);
                }

                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Waits for the process to exit without blocking a UI thread and
        /// returns its exit code.
        /// </summary>
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            var handle = _processInfo.hProcess;

            return Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint result = NativeApi.WaitForSingleObject(handle, 250);
                    if (result == NativeApi.WAIT_OBJECT_0) break;
                    if (result != NativeApi.WAIT_TIMEOUT)
                    {
                        // Handle became invalid (torn down elsewhere).
                        return -1;
                    }
                }

             //   cancellationToken.ThrowIfCancellationRequested();

                uint exitCode;
                return NativeApi.GetExitCodeProcess(handle, out exitCode) ? (int)exitCode : -1;
            }, cancellationToken);
        }

        public void Kill()
        {
            lock (_sync)
            {
                if (_disposed || _processInfo.hProcess == IntPtr.Zero) return;
                if (!HasExitedUnsafe())
                {
                    NativeApi.TerminateProcess(_processInfo.hProcess, 1);
                }
            }
        }

        private bool HasExitedUnsafe()
        {
            return NativeApi.WaitForSingleObject(_processInfo.hProcess, 0) == NativeApi.WAIT_OBJECT_0;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;

                if (_attributeList != IntPtr.Zero)
                {
                    NativeApi.DeleteProcThreadAttributeList(_attributeList);
                    Marshal.FreeHGlobal(_attributeList);
                    _attributeList = IntPtr.Zero;
                }

                if (_processInfo.hThread != IntPtr.Zero)
                {
                    NativeApi.CloseHandle(_processInfo.hThread);
                    _processInfo.hThread = IntPtr.Zero;
                }

                if (_processInfo.hProcess != IntPtr.Zero)
                {
                    NativeApi.CloseHandle(_processInfo.hProcess);
                    _processInfo.hProcess = IntPtr.Zero;
                }
            }
        }
    }
}

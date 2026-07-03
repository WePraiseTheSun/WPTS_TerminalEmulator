using System;
using System.Runtime.InteropServices;

namespace TerminalEmulator.Control.Native
{
    /// <summary>
    /// Reads the live current working directory of another process.
    ///
    /// A PTY host has no protocol-level channel for the shell's CWD (short of
    /// shell-integration escape sequences), so — like other terminal
    /// applications — we read it from the child's PEB:
    /// PEB -> RTL_USER_PROCESS_PARAMETERS -> CurrentDirectory.DosPath.
    ///
    /// This is accurate for shells that keep their Win32 working directory in
    /// sync with `cd` (cmd.exe does). PowerShell tracks its location
    /// internally without changing the process CWD, so for PowerShell this
    /// returns the launch directory (a documented limitation shared by other
    /// terminals without shell integration). Returns null on any failure so
    /// callers can fall back to the profile's start directory.
    /// </summary>
    internal static class ProcessWorkingDirectory
    {
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;

        // x64 offsets: PEB+0x20 = ProcessParameters; +0x38 = CurrentDirectory.DosPath
        // x86 offsets: PEB+0x10 = ProcessParameters; +0x24 = CurrentDirectory.DosPath
        private static readonly int ProcessParametersOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
        private static readonly int CurrentDirectoryOffset = IntPtr.Size == 8 ? 0x38 : 0x24;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;        // in bytes, excluding terminator
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>Best-effort read of the process's current directory; null on failure.</summary>
        public static string TryGet(int processId)
        {
            if (processId <= 0) return null;

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
                if (handle == IntPtr.Zero) return null;

                // A 32-bit host cannot walk a 64-bit child's PEB with these
                // offsets; bail out cleanly rather than reading garbage.
                if (IntPtr.Size == 4 && Environment.Is64BitOperatingSystem)
                {
                    bool childIsWow64;
                    if (!IsWow64Process(handle, out childIsWow64) || !childIsWow64) return null;
                }

                var info = new PROCESS_BASIC_INFORMATION();
                int returnLength;
                if (NtQueryInformationProcess(handle, 0, ref info,
                        Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out returnLength) != 0)
                {
                    return null;
                }
                if (info.PebBaseAddress == IntPtr.Zero) return null;

                IntPtr processParameters = ReadPointer(handle, info.PebBaseAddress + ProcessParametersOffset);
                if (processParameters == IntPtr.Zero) return null;

                var dosPath = ReadStruct<UNICODE_STRING>(handle, processParameters + CurrentDirectoryOffset);
                if (dosPath.Buffer == IntPtr.Zero || dosPath.Length == 0 || dosPath.Length > 0x8000)
                {
                    return null;
                }

                var chars = new byte[dosPath.Length];
                IntPtr read;
                if (!ReadProcessMemory(handle, dosPath.Buffer, chars, (IntPtr)chars.Length, out read) ||
                    read.ToInt64() != chars.Length)
                {
                    return null;
                }

                string path = System.Text.Encoding.Unicode.GetString(chars).TrimEnd('\0');

                // cmd stores the CWD with a trailing backslash (except drive roots).
                if (path.Length > 3 && path.EndsWith("\\", StringComparison.Ordinal))
                {
                    path = path.Substring(0, path.Length - 1);
                }

                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch (Exception)
            {
                return null; // best-effort by contract
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        private static IntPtr ReadPointer(IntPtr process, IntPtr address)
        {
            var buffer = new byte[IntPtr.Size];
            IntPtr read;
            if (!ReadProcessMemory(process, address, buffer, (IntPtr)buffer.Length, out read) ||
                read.ToInt64() != buffer.Length)
            {
                return IntPtr.Zero;
            }
            return IntPtr.Size == 8
                ? (IntPtr)BitConverter.ToInt64(buffer, 0)
                : (IntPtr)BitConverter.ToInt32(buffer, 0);
        }

        private static T ReadStruct<T>(IntPtr process, IntPtr address) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            var buffer = new byte[size];
            IntPtr read;
            if (!ReadProcessMemory(process, address, buffer, (IntPtr)size, out read) ||
                read.ToInt64() != size)
            {
                return default(T);
            }

            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return (T)Marshal.PtrToStructure(pinned.AddrOfPinnedObject(), typeof(T));
            }
            finally
            {
                pinned.Free();
            }
        }
    }
}

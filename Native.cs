using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ECS;

/// <summary>
/// Reading another process's working directory. Windows exposes it only through the
/// process environment block, so this is the one place the widget needs interop.
/// </summary>
internal static class Native {
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation {
        public IntPtr Reserved1, PebBaseAddress, Reserved2, Reserved3, UniqueProcessId, Reserved4;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref ProcessBasicInformation pbi, int len, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const int ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010;

    // x64 offsets into PEB / RTL_USER_PROCESS_PARAMETERS.
    private const int PebProcessParameters = 0x20;
    private const int ParamsCurrentDirectory = 0x38;
    private const int ParamsCommandLine = 0x70;

    private static byte[] Read(IntPtr h, IntPtr addr, int len) {
        var buf = new byte[len];
        return ReadProcessMemory(h, addr, buf, len, out _) ? buf : null;
    }

    /// <summary>Read a UNICODE_STRING sitting at an offset in the parameter block.</summary>
    private static string ReadUnicodeString(IntPtr h, IntPtr pp, int offset, int max) {
        var u = Read(h, pp + offset, 16);
        if (u == null) return null;
        int len = BitConverter.ToUInt16(u, 0);
        if (len <= 0 || len > max) return null;
        var s = Read(h, (IntPtr)BitConverter.ToInt64(u, 8), len);
        return s == null ? null : Encoding.Unicode.GetString(s);
    }

    /// <summary>Working directory and command line of another process, in one open.</summary>
    public static (string Cwd, string CommandLine) Inspect(int pid) {
        var h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (h == IntPtr.Zero) return (null, null);
        try {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), IntPtr.Zero) != 0) return (null, null);
            var p = Read(h, pbi.PebBaseAddress + PebProcessParameters, 8);
            if (p == null) return (null, null);
            var pp = (IntPtr)BitConverter.ToInt64(p, 0);
            return (ReadUnicodeString(h, pp, ParamsCurrentDirectory, 1024),
                    ReadUnicodeString(h, pp, ParamsCommandLine, 4096));
        } catch { return (null, null); } finally { CloseHandle(h); }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PontishApp
{
    public sealed class ConPtyTerminal : IDisposable
    {
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private SafeFileHandle _inPipeWrite;
        private SafeFileHandle _outPipeRead;
        private StreamWriter _writer;
        private Thread _readThread;
        private Thread _waitThread;
        private volatile bool _disposed;

        public string Id { get; }
        public event Action<string, string> OnOutput;
        public event Action<string, int> OnExited;

        public ConPtyTerminal(string id)
        {
            Id = id;
        }

        public void Start(string commandLine, string workingDirectory = null, short cols = 100, short rows = 30, Dictionary<string, string> extraEnv = null)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };

            if (!CreatePipe(out SafeFileHandle inPipeRead, out _inPipeWrite, ref sa, 0))
                throw new InvalidOperationException("CreatePipe(in) failed: " + Marshal.GetLastWin32Error());

            if (!CreatePipe(out _outPipeRead, out SafeFileHandle outPipeWrite, ref sa, 0))
                throw new InvalidOperationException("CreatePipe(out) failed: " + Marshal.GetLastWin32Error());

            try
            {
                var size = new COORD { X = cols, Y = rows };
                int hr = CreatePseudoConsole(size, inPipeRead, outPipeWrite, 0, out _hPC);
                if (hr != 0)
                    throw new InvalidOperationException("CreatePseudoConsole failed: 0x" + hr.ToString("X"));

                var siEx = new STARTUPINFOEX();
                siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

                IntPtr lpSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                siEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);

                try
                {
                    if (!InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref lpSize))
                        throw new InvalidOperationException("InitializeProcThreadAttributeList failed");

                    if (!UpdateProcThreadAttribute(
                        siEx.lpAttributeList,
                        0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        _hPC,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                    {
                        throw new InvalidOperationException("UpdateProcThreadAttribute failed");
                    }

                    var pInfo = new PROCESS_INFORMATION();
                    string dir = string.IsNullOrEmpty(workingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workingDirectory;
                    IntPtr envPtr = BuildEnvironmentBlock(extraEnv);
                    uint flags = EXTENDED_STARTUPINFO_PRESENT | (envPtr != IntPtr.Zero ? 0x00000400u : 0u);
                    try
                    {
                        bool ok = CreateProcessW(
                            null,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            flags,
                            envPtr,
                            dir,
                            ref siEx,
                            out pInfo);

                        if (!ok)
                            throw new InvalidOperationException("CreateProcessW failed: " + Marshal.GetLastWin32Error());
                    }
                    finally
                    {
                        if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
                    }

                    _hProcess = pInfo.hProcess;
                    _hThread = pInfo.hThread;
                }
                finally
                {
                    if (siEx.lpAttributeList != IntPtr.Zero)
                    {
                        DeleteProcThreadAttributeList(siEx.lpAttributeList);
                        Marshal.FreeHGlobal(siEx.lpAttributeList);
                    }
                }
            }
            finally
            {
                inPipeRead.Dispose();
                outPipeWrite.Dispose();
            }

            _writer = new StreamWriter(new FileStream(_inPipeWrite, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };

            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            _waitThread = new Thread(WaitProcessExit) { IsBackground = true };
            _waitThread.Start();
        }

        private static IntPtr BuildEnvironmentBlock(Dictionary<string, string> extraEnv)
        {
            if (extraEnv == null || extraEnv.Count == 0) return IntPtr.Zero;

            var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                if (de.Key is string k && de.Value is string v)
                    merged[k] = v;
            }
            foreach (var kv in extraEnv)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    merged[kv.Key] = kv.Value;
            }

            var sb = new StringBuilder();
            foreach (var kv in merged)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            }
            sb.Append('\0');

            byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        private void WaitProcessExit()
        {
            IntPtr hProc = _hProcess;
            if (hProc == IntPtr.Zero) return;
            WaitForSingleObject(hProc, INFINITE);
            Thread.Sleep(120);
            if (!_disposed)
            {
                var hPC = Interlocked.Exchange(ref _hPC, IntPtr.Zero);
                if (hPC != IntPtr.Zero)
                    try { ClosePseudoConsole(hPC); } catch { }
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var charBuf = new char[4096];

            try
            {
                using var fs = new FileStream(_outPipeRead, FileAccess.Read);
                while (!_disposed)
                {
                    int read = fs.Read(buf, 0, buf.Length);
                    if (read <= 0) break;
                    int chars = decoder.GetChars(buf, 0, read, charBuf, 0, false);
                    if (chars > 0)
                        OnOutput?.Invoke(Id, new string(charBuf, 0, chars));
                }
            }
            catch { }

            int exitCode = 0;
            if (_hProcess != IntPtr.Zero)
                GetExitCodeProcess(_hProcess, out exitCode);

            if (!_disposed)
                OnExited?.Invoke(Id, exitCode);
        }

        public void WriteInput(string data)
        {
            if (_disposed || _writer == null || string.IsNullOrEmpty(data)) return;
            try
            {
                lock (_writer)
                {
                    _writer.Write(data);
                    _writer.Flush();
                }
            }
            catch { }
        }

        public void Resize(short cols, short rows)
        {
            if (_disposed || _hPC == IntPtr.Zero) return;
            if (cols < 10) cols = 10;
            if (rows < 4) rows = 4;
            try { ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows }); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _writer?.Dispose(); } catch { }
            try { _inPipeWrite?.Dispose(); } catch { }
            var hPC = Interlocked.Exchange(ref _hPC, IntPtr.Zero);
            if (hPC != IntPtr.Zero) { try { ClosePseudoConsole(hPC); } catch { } }
            if (_hProcess != IntPtr.Zero)
            {
                try { TerminateProcess(_hProcess, 0); } catch { }
                try { _waitThread?.Join(300); } catch { }
                try { CloseHandle(_hProcess); } catch { }
                _hProcess = IntPtr.Zero;
            }
            if (_hThread != IntPtr.Zero) { try { CloseHandle(_hThread); } catch { } _hThread = IntPtr.Zero; }
            try { _outPipeRead?.Dispose(); } catch { }
        }

        #region Win32 ConPTY P/Invoke
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint INFINITE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, [In] ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        #endregion
    }
}

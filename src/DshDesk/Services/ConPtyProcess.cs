using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DshDesk.Services;

/// <summary>
/// Hosts a console process in a Windows pseudoconsole. No console window is
/// created, while terminal input (notably Ctrl+C) still reaches the child.
/// </summary>
internal sealed class ConPtyProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr PseudoConsoleAttribute = new(0x00020016);
    private static readonly Regex TerminalSequenceRegex = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled);

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Task _outputPump;
    private IntPtr _pseudoConsole;
    private int _ctrlCSent;
    private bool _disposed;

    private ConPtyProcess(
        Process process,
        IntPtr pseudoConsole,
        SafeFileHandle input,
        SafeFileHandle output,
        Action<string> onOutputLine)
    {
        Process = process;
        _pseudoConsole = pseudoConsole;
        _input = new FileStream(input, FileAccess.Write, 4096, isAsync: false);
        _output = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
        _outputPump = Task.Run(() => PumpOutput(onOutputLine));
    }

    public Process Process { get; }

    public Task OutputCompleted => _outputPump;

    public static ConPtyProcess Start(ProcessStartInfo startInfo, Action<string> onOutputLine)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(onOutputLine);
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("ConPTY requires an executable path.", nameof(startInfo));
        }

        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        Process? process = null;
        var processInfo = new ProcessInformation();

        try
        {
            ThrowIfFalse(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0), "Unable to create ConPTY input pipe");
            ThrowIfFalse(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0), "Unable to create ConPTY output pipe");

            var result = CreatePseudoConsole(new Coord(120, 30), inputRead, outputWrite, 0, out pseudoConsole);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            var attributeListSize = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            ThrowIfFalse(
                InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize),
                "Unable to initialize ConPTY process attributes");
            ThrowIfFalse(
                UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    PseudoConsoleAttribute,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero),
                "Unable to attach the pseudoconsole process attribute");

            var executablePath = ResolveExecutablePath(startInfo);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = IntPtr.Zero,
                    StandardOutput = IntPtr.Zero,
                    StandardError = IntPtr.Zero
                },
                AttributeList = attributeList
            };
            var commandLine = BuildCommandLine(startInfo, executablePath);
            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo));
            var creationFlags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment;
            var processAttributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>() };
            var threadAttributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>() };
            ThrowIfFalse(
                CreateProcess(
                    null,
                    commandLine,
                    ref processAttributes,
                    ref threadAttributes,
                    false,
                    creationFlags,
                    environment,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInfo),
                $"Unable to start {executablePath} in ConPTY");

            process = Process.GetProcessById(unchecked((int)processInfo.ProcessId));

            var inputHandle = new SafeFileHandle(inputWrite, ownsHandle: true);
            inputWrite = IntPtr.Zero;
            var outputHandle = new SafeFileHandle(outputRead, ownsHandle: true);
            outputRead = IntPtr.Zero;
            var session = new ConPtyProcess(process, pseudoConsole, inputHandle, outputHandle, onOutputLine);
            process = null;
            pseudoConsole = IntPtr.Zero;
            return session;
        }
        catch
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { /* Startup cleanup. */ }
            }

            process?.Dispose();
            throw;
        }
        finally
        {
            CloseHandleIfNeeded(processInfo.Thread);
            CloseHandleIfNeeded(processInfo.Process);
            CloseHandleIfNeeded(inputRead);
            CloseHandleIfNeeded(inputWrite);
            CloseHandleIfNeeded(outputRead);
            CloseHandleIfNeeded(outputWrite);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    public Task SendCtrlCAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _ctrlCSent, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _input.WriteByte(0x03);
        _input.Flush();
        return Task.CompletedTask;
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo, string executablePath)
    {
        var command = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ').Append(QuoteArgument(argument));
        }

        return command.ToString();
    }

    private static string ResolveExecutablePath(ProcessStartInfo startInfo)
    {
        if (Path.IsPathFullyQualified(startInfo.FileName))
        {
            return startInfo.FileName;
        }

        var path = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("PATH");
        var extensions = Path.HasExtension(startInfo.FileName)
            ? [string.Empty]
            : new[] { string.Empty, ".exe", ".com" };
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), startInfo.FileName + extension));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Unable to resolve executable on PATH: {startInfo.FileName}");
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var environment = new StringBuilder();
        foreach (var pair in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            environment.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        return environment.Append('\0').ToString();
    }

    private void PumpOutput(Action<string> onOutputLine)
    {
        try
        {
            using var reader = new StreamReader(
                _output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                var plainLine = TerminalSequenceRegex.Replace(line, string.Empty);
                onOutputLine(plainLine);
            }
        }
        catch (IOException) when (_disposed)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Dispose();
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        _output.Dispose();
    }

    private static void ThrowIfFalse(bool success, string message)
    {
        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{message} (Win32 error {error})");
        }
    }

    private static void CloseHandleIfNeeded(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            _ = CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(
        Coord size,
        IntPtr input,
        IntPtr output,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        string commandLine,
        ref SecurityAttributes processAttributes,
        ref SecurityAttributes threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        [In] ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

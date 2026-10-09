using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VictusFanControl.Hardware.PawnIo;

/// <summary>
/// Minimal direct client for the PawnIO kernel device.
/// This code does not link against PawnIOLib.dll; it talks to PawnIO only
/// through the documented device I/O control interface.
/// </summary>
internal sealed class PawnIoDevice : IDisposable
{
    private const string DevicePath = @"\\.\PawnIO";

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821u << 2);
    private const uint IoctlExecuteFunction = DeviceType | (0x841u << 2);
    private const uint IoctlVersion = DeviceType | (0x861u << 2);

    private const int FunctionNameLength = 32;

    private readonly SafeFileHandle _handle;

    private PawnIoDevice(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static PawnIoDevice Open()
    {
        var handle = CreateFileW(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"Unable to open {DevicePath}. PawnIO 2.2+ must be installed and the process must be elevated.");
        }

        return new PawnIoDevice(handle);
    }

    public Version GetDriverVersion()
    {
        var output = new byte[sizeof(uint)];
        Ioctl(IoctlVersion, Array.Empty<byte>(), output);

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(output);
        return new Version(
            (int)((raw >> 16) & 0xFF),
            (int)((raw >> 8) & 0xFF),
            (int)(raw & 0xFF));
    }

    public void LoadModule(ReadOnlySpan<byte> moduleBytes)
    {
        if (moduleBytes.IsEmpty)
        {
            throw new ArgumentException("PawnIO module is empty.", nameof(moduleBytes));
        }

        Ioctl(IoctlLoadBinary, moduleBytes.ToArray(), Array.Empty<byte>());
    }

    public ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int outputCount)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("Function name is required.", nameof(functionName));
        }

        var nameBytes = Encoding.ASCII.GetBytes(functionName);
        if (nameBytes.Length >= FunctionNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(functionName), "PawnIO function names must be shorter than 32 bytes.");
        }

        if (outputCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputCount));
        }

        var request = new byte[FunctionNameLength + (input.Length * sizeof(ulong))];
        nameBytes.CopyTo(request, 0);

        for (var i = 0; i < input.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                request.AsSpan(FunctionNameLength + (i * sizeof(ulong)), sizeof(ulong)),
                input[i]);
        }

        var response = new byte[outputCount * sizeof(ulong)];
        var bytesReturned = Ioctl(IoctlExecuteFunction, request, response);

        if (bytesReturned % sizeof(ulong) != 0)
        {
            throw new InvalidDataException($"PawnIO returned {bytesReturned} bytes, not an array of 64-bit cells.");
        }

        var returnedCells = (int)(bytesReturned / sizeof(ulong));
        var values = new ulong[returnedCells];

        for (var i = 0; i < returnedCells; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(
                response.AsSpan(i * sizeof(ulong), sizeof(ulong)));
        }

        return values;
    }

    public void Dispose() => _handle.Dispose();

    private uint Ioctl(uint controlCode, byte[] input, byte[] output)
    {
        if (!DeviceIoControl(
                _handle,
                controlCode,
                input.Length == 0 ? null : input,
                (uint)input.Length,
                output.Length == 0 ? null : output,
                (uint)output.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"PawnIO DeviceIoControl 0x{controlCode:X8} failed.");
        }

        return bytesReturned;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}

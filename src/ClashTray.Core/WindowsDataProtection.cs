using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClashTray.Core;

internal static class WindowsDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;

        public DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }
    }

    public static string ProtectString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] protectedBytes = Protect(StrictUtf8.GetBytes(value));
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("DPAPI 数据格式无效。", exception);
        }

        try
        {
            return StrictUtf8.GetString(Unprotect(protectedBytes));
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException("DPAPI 数据不是有效的 UTF-8。", exception);
        }
    }

    private static byte[] Protect(byte[] plaintext)
    {
        GCHandle inputHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        try
        {
            DataBlob input = new DataBlob(plaintext.Length, inputHandle.AddrOfPinnedObject());
            if (!CryptProtectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob output))
            {
                throw CreateCryptographicException();
            }

            return CopyAndFree(output);
        }
        finally
        {
            inputHandle.Free();
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        GCHandle inputHandle = GCHandle.Alloc(protectedBytes, GCHandleType.Pinned);
        try
        {
            DataBlob input = new DataBlob(protectedBytes.Length, inputHandle.AddrOfPinnedObject());
            if (!CryptUnprotectData(
                    ref input,
                    out IntPtr description,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob output))
            {
                throw CreateCryptographicException();
            }

            if (description != IntPtr.Zero)
            {
                _ = LocalFree(description);
            }

            return CopyAndFree(output);
        }
        finally
        {
            inputHandle.Free();
        }
    }

    private static byte[] CopyAndFree(DataBlob output)
    {
        if (output.Data == IntPtr.Zero || output.Length < 0)
        {
            throw new CryptographicException("DPAPI 返回了无效数据。");
        }

        try
        {
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            _ = LocalFree(output.Data);
        }
    }

    private static CryptographicException CreateCryptographicException() =>
        new CryptographicException($"DPAPI 操作失败，错误码 {Marshal.GetLastWin32Error()}。");

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}

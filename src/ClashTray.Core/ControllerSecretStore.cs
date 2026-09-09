using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClashTray.Core;

public sealed class ControllerSecretStore
{
    private readonly string _secretPath;

    public ControllerSecretStore(AppPaths paths)
    {
        paths.EnsureDirectories();
        _secretPath = paths.ControllerSecretFile;
    }

    public string GetOrCreate()
    {
        if (File.Exists(_secretPath))
        {
            var protectedBytes = File.ReadAllBytes(_secretPath);
            try
            {
                var plainBytes = Unprotect(protectedBytes);
                return Convert.ToBase64String(plainBytes);
            }
            catch (CryptographicException)
            {
                File.Delete(_secretPath);
            }
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = Protect(secret);
        File.WriteAllBytes(_secretPath, protectedSecret);
        return Convert.ToBase64String(secret);
    }

    private static byte[] Protect(byte[] value)
    {
        var input = CreateBlob(value);
        try
        {
            if (!CryptProtectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return ReadAndFreeBlob(output);
        }
        finally
        {
            FreeInputBlob(input.Handle);
        }
    }

    private static byte[] Unprotect(byte[] value)
    {
        var input = CreateBlob(value);
        try
        {
            if (!CryptUnprotectData(ref input.Blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return ReadAndFreeBlob(output);
        }
        finally
        {
            FreeInputBlob(input.Handle);
        }
    }

    private static (DataBlob Blob, GCHandle Handle) CreateBlob(byte[] value)
    {
        var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        return (new DataBlob
        {
            Length = value.Length,
            Data = handle.AddrOfPinnedObject()
        }, handle);
    }

    private static byte[] ReadAndFreeBlob(DataBlob blob)
    {
        try
        {
            var value = new byte[blob.Length];
            if (blob.Length > 0)
            {
                Marshal.Copy(blob.Data, value, 0, blob.Length);
            }

            return value;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                LocalFree(blob.Data);
            }
        }
    }

    private static void FreeInputBlob(GCHandle handle)
    {
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

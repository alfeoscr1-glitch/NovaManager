using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NovaManager;

internal static class DeveloperModeService
{
    private const int PasswordIterations = 600_000;
    private const string UnlockMarker = "Nova Manager developer mode unlocked v1";
    private static readonly byte[] PasswordSalt = Convert.FromBase64String("+2imiVnebuE/PZEgln0lcA==");
    private static readonly byte[] PasswordHash = Convert.FromBase64String("rqFVHfe76+9zWo4akmgY0rsWPWwiCs9y1dS1QY3onpQ=");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Manager developer access marker");
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "developer-mode.dat");

    public static bool IsAuthorized()
    {
        if (!File.Exists(MarkerPath))
        {
            return false;
        }

        var protectedMarker = File.ReadAllBytes(MarkerPath);
        var marker = Unprotect(protectedMarker);
        try
        {
            var expected = Encoding.UTF8.GetBytes(UnlockMarker);
            return CryptographicOperations.FixedTimeEquals(marker, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(marker);
        }
    }

    public static bool TryUnlock(string password)
    {
        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            password,
            PasswordSalt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            PasswordHash.Length);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(candidate, PasswordHash))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }

        var directory = Path.GetDirectoryName(MarkerPath)
            ?? throw new InvalidOperationException("Nova could not determine the developer settings folder.");
        Directory.CreateDirectory(directory);
        var marker = Encoding.UTF8.GetBytes(UnlockMarker);
        var protectedMarker = Protect(marker);
        CryptographicOperations.ZeroMemory(marker);
        var temporaryPath = MarkerPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedMarker);
            File.Move(temporaryPath, MarkerPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedMarker);
        }

        return true;
    }

    private static byte[] Protect(byte[] data)
    {
        var input = CreateBlob(data);
        DataBlob entropy = default;
        try
        {
            entropy = CreateBlob(Entropy);
            if (!CryptProtectData(ref input, "Nova Manager developer access", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 1, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the developer access marker.");
            }

            try
            {
                var protectedData = new byte[output.Size];
                Marshal.Copy(output.Data, protectedData, 0, output.Size);
                return protectedData;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(ref entropy);
            FreeBlob(ref input);
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        var input = CreateBlob(data);
        DataBlob entropy = default;
        try
        {
            entropy = CreateBlob(Entropy);
            if (!CryptUnprotectData(ref input, out var description, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 1, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the saved developer access marker.");
            }

            try
            {
                var clearData = new byte[output.Size];
                Marshal.Copy(output.Data, clearData, 0, output.Size);
                return clearData;
            }
            finally
            {
                ZeroAndFreeLocal(output.Data, output.Size);
                if (description != IntPtr.Zero)
                {
                    LocalFree(description);
                }
            }
        }
        finally
        {
            FreeBlob(ref entropy);
            FreeBlob(ref input);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
            return new DataBlob { Size = data.Length, Data = pointer };
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (blob.Size > 0)
            {
                Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }
    }

    private static void ZeroAndFreeLocal(IntPtr pointer, int size)
    {
        try
        {
            if (pointer != IntPtr.Zero && size > 0)
            {
                Marshal.Copy(new byte[size], 0, pointer, size);
            }
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                LocalFree(pointer);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }
}

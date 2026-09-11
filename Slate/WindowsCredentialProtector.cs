using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Slate.Core;

namespace Slate;

internal sealed class WindowsCredentialProtector : ICredentialProtector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public byte[] Protect(byte[] plaintext, byte[]? entropy = null) => Transform(plaintext, true, entropy);
    public byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null) => Transform(ciphertext, false, entropy);
    public byte[] Protect(byte[] plaintext) => Transform(plaintext, true, null);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, false, null);

    private static byte[] Transform(byte[] input, bool protect, byte[]? entropy)
    {
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle pinEntropy = default;
        IntPtr pEntropyBlob = IntPtr.Zero;
        Blob output = default;
        try
        {
            if (entropy is not null && entropy.Length > 0)
            {
                pinEntropy = GCHandle.Alloc(entropy, GCHandleType.Pinned);
                var entropyBlob = new Blob { Length = entropy.Length, Data = pinEntropy.AddrOfPinnedObject() };
                pEntropyBlob = Marshal.AllocHGlobal(Marshal.SizeOf<Blob>());
                Marshal.StructureToPtr(entropyBlob, pEntropyBlob, false);
            }

            var blob = new Blob { Length = input.Length, Data = pin.AddrOfPinnedObject() };
            bool success = protect
                ? CryptProtectData(ref blob, null, pEntropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, pEntropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);

            if (!success) throw new CryptographicException("Credential protection failed (Windows error " + Marshal.GetLastWin32Error() + ").");
            if (output.Length is <= 0 or > 65536) throw new CryptographicException("Credential protection returned an invalid size.");

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (pEntropyBlob != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pEntropyBlob);
            }
            if (pinEntropy.IsAllocated)
            {
                pinEntropy.Free();
            }
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            pin.Free();
        }
    }
}

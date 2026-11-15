using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PontishApp
{
    public static class CryptoHelper
    {
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("PNTSH01\0");
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int Pbkdf2Iterations = 200_000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] ProtectDpapi(byte[] plainBytes)
        {
            if (plainBytes == null || plainBytes.Length == 0)
                return Array.Empty<byte>();

            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
            inBlob.cbData = plainBytes.Length;

            try
            {
                Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);
                if (!CryptProtectData(ref inBlob, "PontishVault", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new CryptographicException("DPAPI CryptProtectData failed: " + Marshal.GetLastWin32Error());

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static byte[] UnprotectDpapi(byte[] protectedBytes)
        {
            if (protectedBytes == null || protectedBytes.Length == 0)
                return Array.Empty<byte>();

            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            inBlob.pbData = Marshal.AllocHGlobal(protectedBytes.Length);
            inBlob.cbData = protectedBytes.Length;

            try
            {
                Marshal.Copy(protectedBytes, 0, inBlob.pbData, protectedBytes.Length);
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new CryptographicException("DPAPI CryptUnprotectData failed: " + Marshal.GetLastWin32Error());

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static bool IsEncryptedBlob(byte[] data)
        {
            if (data == null || data.Length < MagicHeader.Length + SaltSize + NonceSize + TagSize)
                return false;
            for (int i = 0; i < MagicHeader.Length; i++)
            {
                if (data[i] != MagicHeader[i]) return false;
            }
            return true;
        }

        public static byte[] EncryptWithPassword(byte[] plainBytes, string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required.");

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
            byte[] ciphertext = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSize];

            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plainBytes, ciphertext, tag, MagicHeader);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            byte[] output = new byte[MagicHeader.Length + SaltSize + NonceSize + TagSize + ciphertext.Length];
            int offset = 0;
            Buffer.BlockCopy(MagicHeader, 0, output, offset, MagicHeader.Length); offset += MagicHeader.Length;
            Buffer.BlockCopy(salt, 0, output, offset, SaltSize); offset += SaltSize;
            Buffer.BlockCopy(nonce, 0, output, offset, NonceSize); offset += NonceSize;
            Buffer.BlockCopy(tag, 0, output, offset, TagSize); offset += TagSize;
            Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length);
            return output;
        }

        public static byte[] DecryptWithPassword(byte[] blob, string password)
        {
            if (!IsEncryptedBlob(blob))
                throw new InvalidDataException("Invalid encrypted backup header.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required.");

            int offset = MagicHeader.Length;
            byte[] salt = new byte[SaltSize];
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];
            int cipherLen = blob.Length - (MagicHeader.Length + SaltSize + NonceSize + TagSize);
            byte[] ciphertext = new byte[cipherLen];

            Buffer.BlockCopy(blob, offset, salt, 0, SaltSize); offset += SaltSize;
            Buffer.BlockCopy(blob, offset, nonce, 0, NonceSize); offset += NonceSize;
            Buffer.BlockCopy(blob, offset, tag, 0, TagSize); offset += TagSize;
            Buffer.BlockCopy(blob, offset, ciphertext, 0, cipherLen);

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
            byte[] plain = new byte[cipherLen];

            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plain, MagicHeader);
                return plain;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}

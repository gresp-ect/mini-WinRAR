using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace MiniWinRAR.Core.Crypto;

public class InvalidPasswordException : Exception
{
    public InvalidPasswordException() : base("密码错误") { }
}

public static class CryptoService
{
    public const int SaltLen = 16, NonceLen = 12, TagLen = 16, KeyLen = 32;

    public static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    public static byte[] DeriveKey(string password, byte[] salt)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.DataIndependentAddressing, // Argon2id
            Version = Argon2Version.Nineteen,
            TimeCost = 3, MemoryCost = 65536, Lanes = 4, Threads = 4,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = salt, HashLength = KeyLen,
        };
        using var argon2 = new Argon2(config);
        return argon2.Hash().Buffer.ToArray();
    }

    public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext)
    {
        var ct = new byte[plaintext.Length];
        var tag = new byte[TagLen];
        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, plaintext, ct, tag);
        return ct.Concat(tag).ToArray();
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext)
    {
        try
        {
            var ctLen = ciphertext.Length - TagLen;
            var ct = ciphertext[..ctLen];
            var tag = ciphertext[ctLen..];
            var pt = new byte[ctLen];
            using var aes = new AesGcm(key, TagLen);
            aes.Decrypt(nonce, ct, tag, pt);
            return pt;
        }
        catch (CryptographicException)
        {
            throw new InvalidPasswordException();
        }
    }
}

using System.Text;
using MiniWinRAR.Core.Crypto;

namespace MiniWinRAR.Tests;

public class CryptoServiceTests
{
    [Fact]
    public void Roundtrip_EncryptDecrypt()
    {
        var key = CryptoService.DeriveKey("password", new byte[CryptoService.SaltLen]);
        var nonce = new byte[CryptoService.NonceLen]; // 全 0 可预测，测试用
        var pt = Encoding.UTF8.GetBytes("hello, mini-WinRAR!");
        var ct = CryptoService.Encrypt(key, nonce, pt);
        Assert.Equal(pt.Length + CryptoService.TagLen, ct.Length);
        Assert.Equal(pt, CryptoService.Decrypt(key, nonce, ct));
    }

    [Fact]
    public void WrongKey_Fails()
    {
        var k1 = CryptoService.DeriveKey("right", new byte[CryptoService.SaltLen]);
        var k2 = CryptoService.DeriveKey("wrong", new byte[CryptoService.SaltLen]);
        var ct = CryptoService.Encrypt(k1, new byte[CryptoService.NonceLen], new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidPasswordException>(() => CryptoService.Decrypt(k2, new byte[CryptoService.NonceLen], ct));
    }

    [Fact]
    public void DeriveKey_Deterministic_32Bytes()
    {
        var a = CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen]);
        var b = CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen]);
        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, CryptoService.DeriveKey("pw", new byte[CryptoService.SaltLen].Select(x => (byte)1).ToArray()));
    }
}

using System.Security.Cryptography;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

public class PcpRegisterMessageTests
{
    private static EcKeyPair NewStaticKey() => EcKeyPair.Generate();

    private static (RegisterAck Ack, byte[] Secret, EcKeyPair ClientKeys) NewAck()
    {
        // 模拟服务端：签发 32B deviceSecret 并以客户端 staticPubKey 做 ECIES 封装（OQ-15）
        var clientKeys = NewStaticKey();
        var secret = RandomGenerator.Bytes(32);
        var box = Ecies.Encrypt(clientKeys.ExportPublicKey(), secret);
        var ack = new RegisterAck(1, 100UL, MsgType.RegisterAck,
            Guid.Parse("11111111-2222-3333-4444-555555555555"), box, "0a2b3c", "10.10.0.2",
            [new GroupInfo(Guid.NewGuid(), "默认分组")]);
        return (ack, secret, clientKeys);
    }

    [Fact]
    public void Register_RoundTrip_AllFieldsPreserved()
    {
        var msg = new Register(3, 1000UL, MsgType.Register, "AA:BB:CC:DD:EE:FF", "pc-alpha",
            "windows", "0.1.0", NewStaticKey().ExportPublicKey(),
            InviteCode: null, Username: "alice", Password: "pw123456");
        MsgAssert.Equal(msg, PcpCodec.Decode<Register>(PcpCodec.Encode(msg)));
    }

    [Fact]
    public void Register_OptionalAccountFields_NullSurvives()
    {
        var msg = new Register(4, 1001UL, MsgType.Register, "AA:BB:CC:DD:EE:FF", "pc-beta",
            "linux", "0.1.0", new byte[65],
            InviteCode: "ab12cd", Username: null, Password: null);
        var decoded = PcpCodec.Decode<Register>(PcpCodec.Encode(msg));
        Assert.Null(decoded.Username);
        Assert.Null(decoded.Password);
        Assert.Equal("ab12cd", decoded.InviteCode);
    }

    [Fact]
    public void RegisterAck_CiphertextDecryptRoundTrip()
    {
        var (ack, secret, clientKeys) = NewAck();

        var decoded = PcpCodec.Decode<RegisterAck>(PcpCodec.Encode(ack));
        MsgAssert.Equal(ack, decoded);

        // 客户端以本地静态私钥解开 ECIES 密文 → 得到原始 deviceSecret（OQ-15）
        var decrypted = Ecies.Decrypt(clientKeys, decoded.DeviceSecretBox);
        Assert.True(secret.AsSpan().SequenceEqual(decrypted));
    }

    [Fact]
    public void RegisterAck_TamperedCiphertext_Rejected()
    {
        var (ack, _, clientKeys) = NewAck();
        var wire = PcpCodec.Encode(ack);
        wire[wire.Length / 2] ^= 0x01; // 篡改密文中部

        var decoded = PcpCodec.Decode<RegisterAck>(wire);
        Assert.ThrowsAny<CryptographicException>(() => Ecies.Decrypt(clientKeys, decoded.DeviceSecretBox));
    }

    [Fact]
    public void RegisterAck_WrongKeyCannotDecrypt()
    {
        var (ack, _, _) = NewAck();
        var decoded = PcpCodec.Decode<RegisterAck>(PcpCodec.Encode(ack));
        // 另一台设备的私钥无法解密（密文绑定注册请求提交的 staticPubKey）
        Assert.ThrowsAny<CryptographicException>(() => Ecies.Decrypt(NewStaticKey(), decoded.DeviceSecretBox));
    }

    [Fact]
    public void UnbindMe_RoundTrip()
    {
        var msg = new UnbindMe(5, 2000UL, MsgType.UnbindMe);
        Assert.Equal(msg, PcpCodec.Decode<UnbindMe>(PcpCodec.Encode(msg)));
    }
}

using System.Net;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using Xunit;

namespace P2P.Core.Tests;

public class StunCodecTests
{
    private static readonly Guid DeviceId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    [Fact]
    public void XorMappedAddress_MatchesRfc5769Vector()
    {
        // RFC 5769 §2.2 样例响应中的 XOR-MAPPED-ADDRESS：
        //   attr 00 20 00 08 | value 00 01 a1 47 e1 12 a6 43
        //   → 映射地址 192.0.2.1:32853（cookie=0x2112A442 解异或）
        var transactionId = Convert.FromHexString("b7e7a701bc346890f029a879");
        var wire = StunCodec.BuildBindingResponse(transactionId, IPAddress.Parse("192.0.2.1"), 32853);

        var value = wire.AsSpan(24, 8).ToArray();
        Assert.Equal(Convert.FromHexString("0001a147e112a643"), value);

        Assert.True(StunCodec.TryParseBindingResponse(wire, out var resp));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), resp!.Mapped.Address);
        Assert.Equal(32853, resp.Mapped.Port);
        Assert.True(transactionId.AsSpan().SequenceEqual(resp.TransactionId));
    }

    [Fact]
    public void BindingRequest_DeviceAuth_Verifies()
    {
        var secret = RandomGenerator.Bytes(32);
        var nonce = RandomGenerator.Bytes(16);
        var txn = StunCodec.NewTransactionId();
        var wire = StunCodec.BuildBindingRequest(txn, DeviceId, secret, tsMs: 1_000_000, nonce);

        var fields = StunCodec.ParseDeviceAuthFields(wire);
        Assert.NotNull(fields);
        Assert.Equal(DeviceId, fields!.DeviceId);
        Assert.Equal(1_000_000UL, fields.TsMs);
        Assert.True(nonce.AsSpan().SequenceEqual(fields.Nonce));

        Assert.True(StunCodec.TryParseDeviceAuth(wire, id => id == DeviceId ? secret : null));
    }

    [Fact]
    public void BindingRequest_WrongSecret_SilentlyRejected()
    {
        var txn = StunCodec.NewTransactionId();
        var wire = StunCodec.BuildBindingRequest(txn, DeviceId, RandomGenerator.Bytes(32), 100, RandomGenerator.Bytes(16));
        Assert.False(StunCodec.TryParseDeviceAuth(wire, _ => RandomGenerator.Bytes(32))); // 库中密钥不符
    }

    [Fact]
    public void BindingRequest_UnknownDevice_SilentlyRejected()
    {
        var wire = StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(), DeviceId,
            RandomGenerator.Bytes(32), 100, RandomGenerator.Bytes(16));
        Assert.False(StunCodec.TryParseDeviceAuth(wire, _ => null)); // 未注册 deviceId
    }

    [Fact]
    public void BindingRequest_TamperedHmac_SilentlyRejected()
    {
        var secret = RandomGenerator.Bytes(32);
        var wire = StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(), DeviceId, secret, 100,
            RandomGenerator.Bytes(16));
        wire[^1] ^= 0x01;
        Assert.False(StunCodec.TryParseDeviceAuth(wire, _ => secret));
    }

    [Fact]
    public void Response_Garbage_Rejected()
    {
        Assert.False(StunCodec.TryParseBindingResponse(new byte[10], out _));
        Assert.False(StunCodec.TryParseBindingResponse(new byte[64], out _)); // 长度/魔数不符
        var badMagic = StunCodec.BuildBindingResponse(StunCodec.NewTransactionId(),
            IPAddress.Loopback, 1);
        badMagic[4] ^= 0xFF; // 破坏 magic cookie
        Assert.False(StunCodec.TryParseBindingResponse(badMagic, out _));
    }

    [Fact]
    public void RequestResponse_RoundTrip_OverWire()
    {
        // 客户端发出请求 → 服务端校验认证并以来源地址回包 → 客户端解出映射端点
        var secret = RandomGenerator.Bytes(32);
        var txn = StunCodec.NewTransactionId();
        var request = StunCodec.BuildBindingRequest(txn, DeviceId, secret, 555_000, RandomGenerator.Bytes(16));

        Assert.True(StunCodec.TryParseDeviceAuth(request, id => id == DeviceId ? secret : null));
        var response = StunCodec.BuildBindingResponse(txn, IPAddress.Parse("203.0.113.9"), 40001);

        Assert.True(StunCodec.TryParseBindingResponse(response, out var parsed));
        Assert.Equal("203.0.113.9", parsed!.Mapped.Address.ToString());
        Assert.Equal(40001, parsed.Mapped.Port);
    }
}

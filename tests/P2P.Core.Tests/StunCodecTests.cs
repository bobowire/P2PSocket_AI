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

    // ── M3-15 RFC5780 属性（CHANGE-REQUEST/RESPONSE-ORIGIN/OTHER-ADDRESS，05 §7.2）──

    [Fact]
    public void ChangeRequest_EncodeAndParse_RoundTrip()
    {
        var wire = StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(), DeviceId,
            RandomGenerator.Bytes(32), 100, RandomGenerator.Bytes(16),
            changeFlags: StunCodec.ChangeIpFlag | StunCodec.ChangePortFlag);

        // 属性出现在 DEVICE-AUTH 之后：HeaderLen + 4 + Pad4(72) = 96 处，TLV 头 00 03 00 04 + flags 大端
        Assert.Equal(0x00, wire[96]);
        Assert.Equal(0x03, wire[97]);
        Assert.Equal(StunCodec.ChangeIpFlag | StunCodec.ChangePortFlag, StunCodec.ParseChangeRequest(wire));
    }

    [Fact]
    public void ChangeRequest_Absent_ParsesAsZero()
    {
        var wire = StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(), DeviceId,
            RandomGenerator.Bytes(32), 100, RandomGenerator.Bytes(16));
        Assert.Equal((ushort)0, StunCodec.ParseChangeRequest(wire));
    }

    [Fact]
    public void BindingRequest_WithChange_AuthUnchanged()
    {
        // HMAC 输入=deviceId|nonce|ts|transactionId（02 §3.2），不含属性区——追加 CHANGE-REQUEST 不破坏认证
        var secret = RandomGenerator.Bytes(32);
        var wire = StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(), DeviceId, secret, 100,
            RandomGenerator.Bytes(16), changeFlags: StunCodec.ChangePortFlag);

        Assert.True(StunCodec.TryParseDeviceAuth(wire, id => id == DeviceId ? secret : null));
        Assert.Equal(StunCodec.ChangePortFlag, StunCodec.ParseChangeRequest(wire));
    }

    [Fact]
    public void Response_WithRfc5780Attributes_RoundTrip()
    {
        var txn = StunCodec.NewTransactionId();
        var other = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 3479);
        var origin = new IPEndPoint(IPAddress.Parse("198.51.100.6"), 3478);
        var wire = StunCodec.BuildBindingResponse(txn, IPAddress.Parse("203.0.113.9"), 40001,
            otherAddress: other, responseOrigin: origin);

        Assert.True(StunCodec.TryParseBindingResponse(wire, out var parsed));
        Assert.Equal(other, parsed!.OtherAddress);
        Assert.Equal(origin, parsed.ResponseOrigin);
        Assert.Equal(40001, parsed.Mapped.Port); // 主字段不受新属性影响
    }

    [Fact]
    public void Response_WithoutAttributes_NullFields_ByteIdentical()
    {
        // 默认参数=零属性：输出与 M1 形状逐字节一致（向后兼容，单地址世界零回归）
        var txn = StunCodec.NewTransactionId();
        var legacy = StunCodec.BuildBindingResponse(txn, IPAddress.Parse("203.0.113.9"), 40001);
        var @default = StunCodec.BuildBindingResponse(txn, IPAddress.Parse("203.0.113.9"), 40001,
            otherAddress: null, responseOrigin: null);

        Assert.Equal(legacy, @default);
        Assert.Equal(32, legacy.Length); // 20 头 + 4 属性头 + 8 XOR-MAPPED
        Assert.True(StunCodec.TryParseBindingResponse(legacy, out var parsed));
        Assert.Null(parsed!.OtherAddress);
        Assert.Null(parsed.ResponseOrigin);
    }

    [Fact]
    public void Response_MalformedPlainAttribute_ToleratedAsNull()
    {
        // 畸形 OTHER-ADDRESS（长度 4 非 8）：容忍为 null，不影响 XOR-MAPPED 解析
        var txn = StunCodec.NewTransactionId();
        var wire = StunCodec.BuildBindingResponse(txn, IPAddress.Parse("203.0.113.9"), 40001);
        var extended = new byte[wire.Length + 4 + 4]; // 追加 TLV：type=OTHER len=4 value=垃圾
        wire.CopyTo(extended, 0);
        extended[3] = (byte)(extended[3] + 8); // msgLen 12+8=20（大端低位字节）
        extended[32] = 0x80; extended[33] = 0x2C; extended[34] = 0x00; extended[35] = 0x04;
        extended[36] = 0x00; extended[37] = 0x01; extended[38] = 0xFF; extended[39] = 0xFF;

        Assert.True(StunCodec.TryParseBindingResponse(extended, out var parsed));
        Assert.Null(parsed!.OtherAddress);
        Assert.Equal(40001, parsed.Mapped.Port);
    }
}

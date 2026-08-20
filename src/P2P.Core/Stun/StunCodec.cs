using System.Buffers.Binary;
using System.Net;
using P2P.Core.Crypto;

namespace P2P.Core.Stun;

/// <summary>
/// STUN-R：RFC 5389 Binding Request/Success 子集（02 §3）。
/// 头 20B：type(2) msgLen(2) magic(4)=0x2112A442 transactionId(12)；属性 TLV：type(2) len(2) value(4B 对齐补位)。
/// 全部整数字段网络序（大端）。
/// </summary>
public static class StunCodec
{
    public const ushort BindingRequest = 0x0001;
    public const ushort BindingSuccess = 0x0101;
    public const uint MagicCookie = 0x2112A442;
    public const int HeaderLen = 20;
    public const int TransactionIdLen = 12;

    public const ushort AttrXorMappedAddress = 0x0020;
    public const ushort AttrDeviceAuth = 0x8020;

    public const int DeviceAuthValueLen = 16 + 16 + 8 + Mac.HashLen; // deviceId|nonce|ts|hmac = 72B
    private const int MacHashLen = Mac.HashLen;

    /// <summary>新事务 ID（12B 随机）。</summary>
    public static byte[] NewTransactionId() => RandomGenerator.Bytes(TransactionIdLen);

    // ── 组包 ─────────────────────────────────────────────────────────────

    /// <summary>组 Binding Request，携带 DEVICE-AUTH（02 §3.2：HMAC(deviceSecret, deviceId|nonce|ts|transactionId)）。</summary>
    public static byte[] BuildBindingRequest(ReadOnlySpan<byte> transactionId, Guid deviceId,
        ReadOnlySpan<byte> deviceSecret, ulong tsMs, ReadOnlySpan<byte> nonce16)
    {
        if (transactionId.Length != TransactionIdLen) throw new ArgumentException("事务 ID 须 12B");
        if (nonce16.Length != 16) throw new ArgumentException("nonce 须 16B");

        var auth = new byte[DeviceAuthValueLen];
        deviceId.ToByteArray().CopyTo(auth.AsSpan(0, 16));
        nonce16.CopyTo(auth.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt64BigEndian(auth.AsSpan(32, 8), tsMs);

        // HMAC 输入 = deviceId|nonce|ts|transactionId（02 §3.2）
        var macInput = new byte[40 + TransactionIdLen];
        auth.AsSpan(0, 40).CopyTo(macInput.AsSpan(0, 40));
        transactionId.CopyTo(macInput.AsSpan(40));
        Mac.HmacSha256(deviceSecret, macInput).CopyTo(auth.AsSpan(40));

        var wire = new byte[HeaderLen + 4 + Pad4(DeviceAuthValueLen)];
        WriteHeader(wire, BindingRequest, transactionId, attrLen: 4 + DeviceAuthValueLen);
        WriteAttrHeader(wire.AsSpan(HeaderLen), AttrDeviceAuth, DeviceAuthValueLen);
        auth.CopyTo(wire.AsSpan(HeaderLen + 4));
        return wire;
    }

    /// <summary>组 Binding Success Response：仅含 XOR-MAPPED-ADDRESS。</summary>
    public static byte[] BuildBindingResponse(ReadOnlySpan<byte> transactionId, IPAddress mapped, ushort port)
    {
        var value = EncodeXorMappedAddress(mapped, port);
        var wire = new byte[HeaderLen + 4 + Pad4(value.Length)];
        WriteHeader(wire, BindingSuccess, transactionId, attrLen: 4 + value.Length);
        WriteAttrHeader(wire.AsSpan(HeaderLen), AttrXorMappedAddress, (ushort)value.Length);
        value.CopyTo(wire.AsSpan(HeaderLen + 4));
        return wire;
    }

    // ── 解包 ─────────────────────────────────────────────────────────────

    public sealed record BindingResponse(byte[] TransactionId, IPEndPoint Mapped);

    /// <summary>解析 Success Response（客户端侧）：校验 magic/type，取首个 XOR-MAPPED-ADDRESS。</summary>
    public static bool TryParseBindingResponse(ReadOnlySpan<byte> wire, out BindingResponse? response)
    {
        response = null;
        if (wire.Length < HeaderLen || !HeaderOk(wire, BindingSuccess))
            return false;

        var transactionId = wire.Slice(8, TransactionIdLen).ToArray();
        if (!TryGetAttribute(wire, AttrXorMappedAddress, out var value) || value.Length != 8 || value[1] != 0x01)
            return false; // 仅 IPv4（02 §3 M1 范围）
        var xPort = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        var port = (ushort)(xPort ^ (MagicCookie >> 16));
        var addr = new byte[4]; // 逐字节与 cookie 异或后按网络序构造（IPAddress(uint) 是小端语义，不可用）
        for (var i = 0; i < 4; i++)
            addr[i] = (byte)(value[4 + i] ^ ((MagicCookie >> (24 - 8 * i)) & 0xFF));
        response = new BindingResponse(transactionId, new IPEndPoint(new IPAddress(addr), port));
        return true;
    }

    public sealed record DeviceAuth(Guid DeviceId, byte[] Nonce, ulong TsMs, byte[] Hmac, byte[] TransactionId);

    /// <summary>提取并校验 DEVICE-AUTH（服务端侧）；结构/HMAC 任一不符返回 false（静默丢弃，02 §3.2）。</summary>
    public static bool TryParseDeviceAuth(ReadOnlySpan<byte> wire, Func<Guid, byte[]?> secretByDevice)
    {
        if (wire.Length < HeaderLen || !HeaderOk(wire, BindingRequest))
            return false;
        var transactionId = wire.Slice(8, TransactionIdLen);

        if (!TryGetAttribute(wire, AttrDeviceAuth, out var value) || value.Length != DeviceAuthValueLen)
            return false;
        var deviceId = new Guid(value.Slice(0, 16).ToArray());
        var secret = secretByDevice(deviceId);
        if (secret is null) return false; // 未注册设备：静默丢弃

        var expected = Mac.HmacSha256(secret, BuildAuthMacInput(value, transactionId));
        return Mac.Verify(value.Slice(40, MacHashLen), expected);
    }

    /// <summary>DEVICE-AUTH 字段拆解（服务端校验 ts 窗口/nonce 去重用）。</summary>
    public static DeviceAuth? ParseDeviceAuthFields(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < HeaderLen || !HeaderOk(wire, BindingRequest))
            return null;
        var transactionId = wire.Slice(8, TransactionIdLen);
        if (!TryGetAttribute(wire, AttrDeviceAuth, out var value) || value.Length != DeviceAuthValueLen)
            return null;
        return new DeviceAuth(
            new Guid(value.Slice(0, 16).ToArray()),
            value.Slice(16, 16).ToArray(),
            BinaryPrimitives.ReadUInt64BigEndian(value.Slice(32, 8)),
            value.Slice(40, MacHashLen).ToArray(),
            transactionId.ToArray());
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    /// <summary>XOR-MAPPED-ADDRESS 编码：family(1)=0x01 + port^(cookie>>16) + addr^cookie（RFC 5389 §15.2）。</summary>
    private static byte[] EncodeXorMappedAddress(IPAddress mapped, ushort port)
    {
        var addrBytes = mapped.GetAddressBytes();
        if (addrBytes.Length != 4) throw new ArgumentException("M1 仅支持 IPv4");
        var value = new byte[8];
        value[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2, 2), (ushort)(port ^ (MagicCookie >> 16)));
        for (var i = 0; i < 4; i++)
            value[4 + i] = (byte)(addrBytes[i] ^ ((MagicCookie >> (24 - 8 * i)) & 0xFF));
        return value;
    }

    private static byte[] BuildAuthMacInput(ReadOnlySpan<byte> authValue72, ReadOnlySpan<byte> transactionId)
    {
        var input = new byte[40 + TransactionIdLen];
        authValue72.Slice(0, 40).CopyTo(input.AsSpan(0, 40)); // deviceId|nonce|ts
        transactionId.CopyTo(input.AsSpan(40));
        return input;
    }

    private static bool HeaderOk(ReadOnlySpan<byte> wire, ushort expectedType)
    {
        var type = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(0, 2));
        var msgLen = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(wire.Slice(4, 4));
        return type == expectedType && cookie == MagicCookie
            && wire.Length == HeaderLen + msgLen; // UDP 报文长度即消息长度
    }

    private static void WriteHeader(Span<byte> dst, ushort type, ReadOnlySpan<byte> transactionId, int attrLen)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2), (ushort)attrLen);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), MagicCookie);
        transactionId.CopyTo(dst.Slice(8, TransactionIdLen));
    }

    private static void WriteAttrHeader(Span<byte> dst, ushort type, ushort len)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2), len);
    }

    /// <summary>取首个匹配属性；属性区越界/截断视为不存在（容忍解析）。</summary>
    private static bool TryGetAttribute(ReadOnlySpan<byte> wire, ushort attrType, out ReadOnlySpan<byte> value)
    {
        value = default;
        var offset = HeaderLen;
        while (offset + 4 <= wire.Length)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(offset, 2));
            var len = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(offset + 2, 2));
            var valueStart = offset + 4;
            if (valueStart + len > wire.Length)
                return false; // 长度越界：截断容错
            if (type == attrType)
            {
                value = wire.Slice(valueStart, len);
                return true;
            }
            offset = valueStart + Pad4(len);
        }
        return false;
    }

    private static int Pad4(int len) => (len + 3) & ~3;
}

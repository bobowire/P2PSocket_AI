// M1-22 客户端本机状态（03 §5）：state.json 的内存形态。
// 机密字段（deviceSecret/静态私钥）明文仅驻内存，落盘经 ISecretProtector（07 §4/SEC-23）。
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

/// <summary>设备注册态：未注册时字段全空，注册成功后由向导后端填充（M1-24）。</summary>
public sealed class ClientState
{
    /// <summary>服务端签发的设备身份（权威身份，D6）。</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>deviceSecret（32B，07 §4：控制通道 HMAC/STUN 认证）。明文仅驻内存，落盘加密。</summary>
    public byte[]? DeviceSecret { get; set; }

    /// <summary>静态密钥对私钥（P-256 PKCS#8；07 §4：隧道握手身份绑定）。明文仅驻内存，落盘加密。</summary>
    public byte[]? StaticPrivateKey { get; set; }

    /// <summary>远程码（6 位，服务端分配）。</summary>
    public string? RemoteCode { get; set; }

    /// <summary>虚拟 IP（统一下发固定 .2，OQ-13）。</summary>
    public string? VirtualIp { get; set; }

    /// <summary>是否已注册（网卡创建与自动登录的前提，A-1/A-2 场景）。</summary>
    [JsonIgnore]
    public bool IsRegistered =>
        DeviceId is not null && DeviceSecret is not null && !string.IsNullOrEmpty(VirtualIp);
}

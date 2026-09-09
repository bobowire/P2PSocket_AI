using P2P.Core.Tunnel;

namespace P2P.Client.Tunnel;

/// <summary>
/// PTP 帧传输通道（M1-25）：打洞成功后的承载绑定（02 §4.5——UDP 打洞成功→UDP 承载，
/// TCP→TCP 承载；M2 中继同一接口）。TunnelSession 与路径解耦（05 §4：加密与路径解耦），
/// M1-26 打洞产物提供 UDP 实现，单测用内存管道对。
/// </summary>
public interface ITunnelTransport : IAsyncDisposable
{
    /// <summary>发送一个完整 PTP 帧（wire bytes）。实现保证线程安全或调用方串行。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    /// <summary>接收一个完整 PTP 帧；null = 传输已关闭（触发会话销毁）。</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>channel 事件处理（M1-25 定义，M1-27 MappingEngine 实现挂载）。</summary>
public interface ITunnelChannelHandler
{
    /// <summary>收到 OPEN（本端为目标侧：对端请求打开目标连接）。</summary>
    void OnOpen(uint channelId, OpenPayload open);

    /// <summary>收到 OPEN_OK / OPEN_FAIL（本端为访问侧）。</summary>
    void OnOpenResult(uint channelId, OpenResultPayload result);

    /// <summary>收到 DATA。</summary>
    void OnData(uint channelId, ReadOnlyMemory<byte> data);

    /// <summary>收到 CLOSE（channel 结束；正常/异常不区分载荷）。</summary>
    void OnClose(uint channelId);
}

/// <summary>channel 事件空实现（测试/未挂载引擎时）。</summary>
public sealed class NullChannelHandler : ITunnelChannelHandler
{
    public static NullChannelHandler Instance { get; } = new();
    public void OnOpen(uint channelId, OpenPayload open) { }
    public void OnOpenResult(uint channelId, OpenResultPayload result) { }
    public void OnData(uint channelId, ReadOnlyMemory<byte> data) { }
    public void OnClose(uint channelId) { }
}

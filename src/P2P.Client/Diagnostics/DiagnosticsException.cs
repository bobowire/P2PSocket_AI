namespace P2P.Client.Diagnostics;

/// <summary>诊断族异常基类（M3-16：stun-test/ping-device 共用错误通道）——
/// Web 层 Api.Fail switch 以本类型透传 code（不反向依赖：诊断器自身不 import Web）。</summary>
public class DiagnosticsException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

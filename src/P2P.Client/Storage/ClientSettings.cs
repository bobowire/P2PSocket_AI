// M1-22 客户端配置（08 §5.2，首启向导写入、本地 Web 可改 04 §2.1）：
// 字段与默认值逐项对照设计，不新增公开配置键（任务清单 M1-22 注：集成测试 STUN 地址走
// in-proc 配置注入，不落本文件）。
namespace P2P.Client.Storage;

/// <summary>settings.json 内存形态。校验规则见 <see cref="SettingsStore"/>（NFR-35）。</summary>
public sealed class ClientSettings
{
    /// <summary>服务端候选地址（host:port，域名/主备多候选，依次尝试 FR-C-604）。</summary>
    public string[] ServerAddrs { get; set; } = [];

    /// <summary>本地 Web 端口（默认 7100）。</summary>
    public int LocalWebPort { get; set; } = 7100;

    /// <summary>本地 Web token 鉴权开关（SEC-82/M5-12 可配置化）。默认 <b>false</b>=免鉴权出厂可用
    /// （2026-10-24 v1.83 用户裁定：服务形态无 token 自动入口——Interactive=console 才自动开浏览器，
    /// 且 web-token 文件 ACL=SYSTEM/管理员，非管理员桌面进程无干净送达通道——每台新装机都会卡死首访；
    /// 需要加固的机器在设置页显式开启，属 opt-in 加固）。中间件启动期装配，变更重启生效。</summary>
    public bool WebTokenAuth { get; set; } = false;

    /// <summary>打洞并发数（1~5，OQ-1 决议，默认 3）。</summary>
    public int PunchConcurrency { get; set; } = 3;

    /// <summary>KCP 承载开关（M6-04 kill-switch，NET-77，设计 11 §8.3）：true=打洞信令上送
    /// CAP_KCP 位（对端同开启且支持时 UDP 直连承载升级 KCP 可靠层）；false=不上送位 → 本端
    /// 判定恒 false，强制裸 UDP（双角色同效，混布降级同形）。默认 true。</summary>
    public bool KcpBearer { get; set; } = true;

    /// <summary>隧道保活间隔秒（默认 20）。</summary>
    public int KeepaliveSec { get; set; } = 20;

    /// <summary>断线重连退避（默认 1s→30s 指数）。</summary>
    public ReconnectSettings Reconnect { get; set; } = new();
}

/// <summary>重连退避参数（08 §5.2 reconnect 节）。</summary>
public sealed class ReconnectSettings
{
    public int MinSec { get; set; } = 1;
    public int MaxSec { get; set; } = 30;
}

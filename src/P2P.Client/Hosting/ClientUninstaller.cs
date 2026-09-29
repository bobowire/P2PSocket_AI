// M2-25 卸载清理（FR-C-203、A-11 实机验收 M3 轮执行、05 §1.2「驱动适配器随卸载删除」）：
// --uninstall CLI 编排——①可选控制通道解绑确认（0x12 UnbindMe：服务端删除设备及关联行后断连，
// 观察连接离开 Established 即确认）；②移除虚拟网卡（RemoveAsync 本实例句柄 + RemoveLeftoverAsync
// 遗留适配器——Wintun.dll 随进程加载不可自删，卸载器/A-11 流程负责删除程序目录 dll）；
// ③配置目录按参数保留（--keep-config，默认）或删除（--purge）。
// 映射监听不在此释放：CLI 为独立短命进程自身无监听，运行中服务的监听随服务停止
//（安装器序）/ClientRuntime.DisposeAsync 释放（集成测按此口径断言端口已自由）。
using System.Net;
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Protocol;
using P2P.Nic;

namespace P2P.Client.Hosting;

/// <summary>卸载选项（--uninstall CLI 与测试同入口）。</summary>
public sealed record UninstallOptions
{
    /// <summary>配置基目录（03 §5；默认 ClientPaths.DefaultBaseDir）。</summary>
    public string BaseDir { get; init; } = ClientPaths.DefaultBaseDir;

    /// <summary>控制通道解绑确认（0x12）：默认 false（仅本地清理）。</summary>
    public bool Unbind { get; init; }

    /// <summary>删除配置目录（--purge）；默认 false=--keep-config 保留。</summary>
    public bool Purge { get; init; }

    /// <summary>网卡替身缝（集成测试注入）。</summary>
    public INicManager? NicOverride { get; init; }

    /// <summary>解绑链路预算（连接建立 + 发送 + 断连观察）。</summary>
    public TimeSpan UnbindTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>卸载执行报告（逐项结果，失败不连坐——单项异常记 Notes 继续）。</summary>
public sealed record UninstallReport(
    bool UnbindConfirmed, string? UnbindError,
    bool NicRemoved, bool LeftoverRemoved,
    bool ConfigDeleted, IReadOnlyList<string> Notes);

/// <summary>卸载清理编排（M2-25）：独立进程执行，不依赖运行中的 ClientRuntime。</summary>
public static class ClientUninstaller
{
    public static async Task<UninstallReport> RunAsync(UninstallOptions options, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var unbindConfirmed = false;
        string? unbindError = null;

        // ① 0x12 解绑确认（可选；须已注册且配置含服务端地址）
        if (options.Unbind)
        {
            try
            {
                unbindConfirmed = await TryUnbindAsync(options, notes, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                unbindError = e.Message;
                notes.Add($"解绑未完成：{e.Message}（服务端记录仍在——如需彻底解绑请服务端 0x12/管理员解绑）");
            }
        }

        // ② 虚拟网卡移除：本实例句柄（CLI 进程通常无）+ 遗留适配器
        var nic = options.NicOverride ?? NicManagerFactory.Create();
        var nicRemoved = false;
        try
        {
            await nic.RemoveAsync(ct);
            nicRemoved = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add($"网卡句柄清理失败：{e.Message}");
        }
        var leftoverRemoved = false;
        try
        {
            leftoverRemoved = await nic.RemoveLeftoverAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add($"遗留适配器移除失败：{e.Message}（Wintun.dll 卸载由安装器/A-11 流程执行）");
        }
        if (nic is IAsyncDisposable d) await d.DisposeAsync();

        // ③ 配置目录：--purge 删除 / 默认 --keep-config 保留
        var configDeleted = false;
        if (options.Purge)
        {
            try
            {
                if (Directory.Exists(options.BaseDir)) Directory.Delete(options.BaseDir, recursive: true);
                configDeleted = !Directory.Exists(options.BaseDir);
                if (!configDeleted) notes.Add("配置目录删除后仍存在（文件被占用？请停止服务后重跑 --purge）");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notes.Add($"配置目录删除失败：{e.Message}（日志文件被本进程/服务占用——停止服务后重跑 --purge）");
            }
        }
        else
        {
            notes.Add($"--keep-config：配置目录已保留（{options.BaseDir}）");
        }

        return new UninstallReport(unbindConfirmed, unbindError, nicRemoved, leftoverRemoved, configDeleted, notes);
    }

    /// <summary>0x12 解绑：建控制通道（Hello 凭据自动认证）→ Established 后发送 UnbindMe →
    /// 服务端删行断连（无 Ack 消息，02 §2.4）→ 观察连接离开 Established 即确认。</summary>
    private static async Task<bool> TryUnbindAsync(UninstallOptions options, List<string> notes, CancellationToken ct)
    {
        var state = new StateStore(options.BaseDir);
        state.Load();
        var settings = new SettingsStore(options.BaseDir);
        settings.Load();
        var addrs = settings.Settings.ServerAddrs;
        if (!state.State.IsRegistered || state.State.DeviceId is not { } deviceId
            || state.State.DeviceSecret is not { Length: > 0 } secret || addrs.Length == 0)
        {
            notes.Add("解绑跳过：本地无已注册凭据或服务端地址");
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.UnbindTimeout);
        var control = new ControlClient(addrs, new ControlClientOptions(),
            deviceId: deviceId, deviceSecret: secret);
        try
        {
            while (control.State != ControlClientState.Established)
            {
                if (timeoutCts.Token.IsCancellationRequested
                    || control.State is ControlClientState.Stopped or ControlClientState.NeedRegister)
                    throw new ControlClientException($"控制通道未建立（{control.State}），0x12 未发送");
                await Task.Delay(50, timeoutCts.Token);
            }

            await control.SendAsync(new UnbindMe(control.NextSeq(), control.TimestampMs(),
                MsgType.UnbindMe), timeoutCts.Token);

            // 服务端删行后 CloseAsync("unbound")：连接离开 Established（凭据已删不可再 Established）
            while (control.State == ControlClientState.Established && !timeoutCts.Token.IsCancellationRequested)
                await Task.Delay(50, timeoutCts.Token);
            var confirmed = control.State != ControlClientState.Established;
            if (confirmed) notes.Add("0x12 解绑确认：服务端已删除设备及关联记录（连接已断）");
            else notes.Add("0x12 已发送但未观察到断连（超时）——请到服务端核验解绑结果");
            return confirmed;
        }
        finally
        {
            await control.DisposeAsync();
        }
    }
}

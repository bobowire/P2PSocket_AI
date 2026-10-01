// M1-28 映射同步层（04 §2.5 / FR-C-304/305 / FR-S-801）：
// - CRUD 入口：本地校验 → 0x60/0x61 全量同步服务端（服务端 L2 再校验+持久化）→ MappingEngine 启停 → state.json 落盘；
// - RemoteCode（用户语义）→ PeerDeviceId（引擎语义）经 0x40 可见设备分页解析（低频操作不缓存，
//   列表口径=L2 同源：本账号 ∪ 共同分组）；
// - 列表视图 = state.json 配置 ∪ 引擎快照状态（punching|direct|relay|failed|disabled|invalid，04 §2.8）；
// - 状态变迁转发（M1-29 WS mapping_state 事件源）。
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Mapping;

/// <summary>映射业务异常（API 层转 envelope code；码表=04 §5）。</summary>
public sealed class MappingException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>映射列表视图（04 §2.5：配置字段 + 实时状态）。</summary>
public sealed record MappingView(
    Guid MappingId,
    string Name,
    ushort LocalPort,
    string Proto,
    string TargetRemoteCode,
    string TargetAddr,
    ushort TargetPort,
    bool Enabled,
    string State,
    string? Detail);

/// <summary>映射流量视图（04 §2.8 mapping_stats 事件源：累计字节 + 当前路径）。</summary>
public sealed record MappingTrafficView(Guid MappingId, long BytesUp, long BytesDown, string Path);

/// <summary>按映射统计行（FR-C-1002 汇总）：配置字段 + 本地引擎累计（up/down/其中中继）+ 当前路径。</summary>
public sealed record MappingStatsView(Guid MappingId, string Name, string Proto, ushort LocalPort,
    string TargetRemoteCode, ushort TargetPort, string Path, long BytesUp, long BytesDown, long RelayBytes);

/// <summary>按设备统计行（FR-C-1002 汇总）：目标远程码聚合（映射数 + 三向字节和）。</summary>
public sealed record DeviceStatsView(string TargetRemoteCode, int Mappings, long BytesUp, long BytesDown,
    long RelayBytes);

/// <summary>流量汇总（FR-C-1002）：本地引擎累计口径——进程重启清零如实反映（M2-22 目标侧归对端映射）。</summary>
public sealed record StatsSummaryView(MappingStatsView[] ByMappings, DeviceStatsView[] ByDevices,
    long TotalBytesUp, long TotalBytesDown, long TotalRelayBytes);

/// <summary>
/// 映射同步服务：CRUD 编排（校验→服务端同步→引擎启停→落盘）。
/// 写顺序=服务端先行（拒绝则本地不动，事务性），引擎动作在后。
/// </summary>
public sealed class MappingSyncService
{
    private readonly ControlClient _control;
    private readonly MappingEngine _engine;
    private readonly StateStore _store;
    private readonly SemaphoreSlim _mutex = new(1, 1); // CRUD 串行（state 列表读改写）

    public MappingSyncService(ControlClient control, MappingEngine engine, StateStore store)
    {
        _control = control;
        _engine = engine;
        _store = store;
        _engine.StateChanged += e => StateChanged?.Invoke(e);
    }

    /// <summary>状态变迁转发（M1-29 WS mapping_state 事件源）。</summary>
    public event Action<MappingStateEvent>? StateChanged;

    // ── 查询 ─────────────────────────────────────────────────────────

    /// <summary>映射列表（state.json 配置 + 引擎实时状态合并）。</summary>
    public IReadOnlyList<MappingView> List()
    {
        var snapshots = _engine.Snapshots.ToDictionary(s => s.Config.MappingId);
        return _store.State.Mappings
            .Select(m =>
            {
                var snap = snapshots.GetValueOrDefault(m.MappingId);
                return new MappingView(m.MappingId, m.Name, m.LocalPort, m.Proto,
                    m.TargetRemoteCode, m.TargetAddr, m.TargetPort, m.Enabled,
                    StateString(snap?.State), snap?.Detail);
            })
            .OrderBy(m => m.Name).ThenBy(m => m.MappingId)
            .ToList();
    }

    /// <summary>流量视图（启用中映射；WS hub 1s 采样算速率，04 §2.8）。</summary>
    public IReadOnlyList<MappingTrafficView> Traffic()
    {
        var states = _engine.Snapshots.ToDictionary(s => s.Config.MappingId, s => s.State);
        return _engine.TrafficSnapshots()
            .Select(t => new MappingTrafficView(t.MappingId, t.BytesUp, t.BytesDown,
                StateString(states.GetValueOrDefault(t.MappingId))))
            .ToList();
    }

    /// <summary>流量汇总（FR-C-1002）：全量映射（含停用——零值行）× 引擎累计 join，
    /// 经 <see cref="BuildSummary"/> 两维聚合。</summary>
    public StatsSummaryView Summary()
    {
        var traffic = _engine.TrafficSnapshots().ToDictionary(t => t.MappingId);
        var states = _engine.Snapshots.ToDictionary(s => s.Config.MappingId, s => s.State);
        var rows = _store.State.Mappings.Select(m =>
        {
            var t = traffic.GetValueOrDefault(m.MappingId);
            return new MappingStatsView(m.MappingId, m.Name, m.Proto, m.LocalPort, m.TargetRemoteCode,
                m.TargetPort, StateString(states.GetValueOrDefault(m.MappingId)),
                t?.BytesUp ?? 0, t?.BytesDown ?? 0, t?.RelayBytes ?? 0);
        }).ToList();
        return BuildSummary(rows);
    }

    /// <summary>两维聚合（纯函数供单测）：映射行序=名称+Id（与 <see cref="List"/> 同口径）；
    /// 设备维按目标远程码分组求和、远程码字典序；总计=映射维全量和（=设备维全量和）。</summary>
    internal static StatsSummaryView BuildSummary(IReadOnlyList<MappingStatsView> rows)
    {
        var byMappings = rows.OrderBy(m => m.Name).ThenBy(m => m.MappingId).ToArray();
        var byDevices = rows.GroupBy(m => m.TargetRemoteCode)
            .Select(g => new DeviceStatsView(g.Key, g.Count(),
                g.Sum(x => x.BytesUp), g.Sum(x => x.BytesDown), g.Sum(x => x.RelayBytes)))
            .OrderBy(d => d.TargetRemoteCode)
            .ToArray();
        return new StatsSummaryView(byMappings, byDevices,
            rows.Sum(m => m.BytesUp), rows.Sum(m => m.BytesDown), rows.Sum(m => m.RelayBytes));
    }

    /// <summary>枚举 → 状态字符串（04 §2.8 mapping_state.state 同表）。</summary>
    internal static string StateString(MappingState? state) => state switch
    {
        MappingState.Punching => "punching",
        MappingState.Direct => "direct",
        MappingState.Relay => "relay",
        MappingState.Failed => "failed",
        MappingState.Invalid => "invalid",
        _ => "disabled",
    };

    // ── 创建 ─────────────────────────────────────────────────────────

    /// <summary>新建映射（Enabled=false 落库；启用走 <see cref="EnableAsync"/>）。</summary>
    public async Task<StoredMapping> CreateAsync(string name, ushort localPort, string targetRemoteCode,
        ushort targetPort, string proto = "tcp", string targetAddr = "self", CancellationToken ct = default)
    {
        var clean = Validate(name, localPort, targetRemoteCode, targetPort, proto, targetAddr);
        await _mutex.WaitAsync(ct);
        try
        {
            // 本地预检：同协议本地端口唯一（服务端 UNIQUE 同口径，先拒省一次往返）
            if (_store.State.Mappings.Any(m => m.Proto == clean.Proto && m.LocalPort == clean.LocalPort))
                throw new MappingException(ErrorCode.Conflict, "同协议本地端口已被占用");

            var peer = await ResolvePeerAsync(clean.TargetRemoteCode, ct);
            var ack = await _control.SendRequestAsync<MappingUpsertAck>(new MappingUpsert(
                _control.NextSeq(), _control.TimestampMs(), MsgType.MappingUpsert,
                null, clean.Name, clean.LocalPort, clean.Proto, clean.TargetRemoteCode,
                clean.TargetAddr, clean.TargetPort, Enabled: false), ct);

            var stored = new StoredMapping(ack.MappingId, clean.Name, clean.LocalPort, clean.Proto,
                clean.TargetRemoteCode, clean.TargetAddr, clean.TargetPort, false);
            _store.State.Mappings.Add(stored);
            await _store.SaveAsync(ct);
            return stored;
        }
        finally { _mutex.Release(); }
    }

    // ── 启用/停用 ────────────────────────────────────────────────────

    /// <summary>启用：0x60 Enabled=true → 引擎监听+打洞排队 → 落盘。</summary>
    public async Task EnableAsync(Guid mappingId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var m = Find(mappingId);
            var peer = await ResolvePeerAsync(m.TargetRemoteCode, ct);
            await UpsertServerAsync(m, enabled: true, ct);

            await _engine.EnableAsync(new MappingConfig(m.MappingId, m.Name, m.LocalPort, m.Proto,
                m.TargetAddr, m.TargetPort, peer.DeviceId));
            UpdateStored(m, enabled: true);
            await _store.SaveAsync(ct);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>停用：0x60 Enabled=false → 引擎拆监听 → 落盘。</summary>
    public async Task DisableAsync(Guid mappingId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var m = Find(mappingId);
            await UpsertServerAsync(m, enabled: false, ct);

            await _engine.DisableAsync(mappingId);
            UpdateStored(m, enabled: false);
            await _store.SaveAsync(ct);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>失败手动重试（04 §2.5）：引擎态 failed→重排打洞；纯本地动作不经服务端。</summary>
    public Task RetryAsync(Guid mappingId, CancellationToken ct = default)
    {
        Find(mappingId); // 未知 id → 1002（与启停同口径）
        return _engine.RetryAsync(mappingId);
    }

    // ── 更新/删除 ────────────────────────────────────────────────────

    /// <summary>更新：启用态改本地端口/协议 → 1003（与服务端规则同口径，04 §2.5）；
    /// 启用中的其余字段变更先停引擎，服务端确认后按新配置重启。</summary>
    public async Task<StoredMapping> UpdateAsync(Guid mappingId, string name, ushort localPort,
        string targetRemoteCode, ushort targetPort, string proto = "tcp", string targetAddr = "self",
        CancellationToken ct = default)
    {
        var clean = Validate(name, localPort, targetRemoteCode, targetPort, proto, targetAddr);
        await _mutex.WaitAsync(ct);
        try
        {
            var m = Find(mappingId);
            if (m.Enabled && (m.LocalPort != clean.LocalPort || m.Proto != clean.Proto))
                throw new MappingException(ErrorCode.Conflict, "启用状态下不允许修改本地端口/协议，请先停用");
            if (_store.State.Mappings.Any(x => x.MappingId != mappingId
                    && x.Proto == clean.Proto && x.LocalPort == clean.LocalPort))
                throw new MappingException(ErrorCode.Conflict, "同协议本地端口已被占用");

            var peer = await ResolvePeerAsync(clean.TargetRemoteCode, ct);
            var wasEnabled = m.Enabled;
            if (wasEnabled) await _engine.DisableAsync(mappingId); // 配置变更须重启监听

            await _control.SendRequestAsync<MappingUpsertAck>(new MappingUpsert(
                _control.NextSeq(), _control.TimestampMs(), MsgType.MappingUpsert,
                mappingId, clean.Name, clean.LocalPort, clean.Proto, clean.TargetRemoteCode,
                clean.TargetAddr, clean.TargetPort, wasEnabled), ct);

            var updated = m with
            {
                Name = clean.Name, LocalPort = clean.LocalPort, Proto = clean.Proto,
                TargetRemoteCode = clean.TargetRemoteCode, TargetAddr = clean.TargetAddr,
                TargetPort = clean.TargetPort,
            };
            ReplaceStored(updated);
            if (wasEnabled)
            {
                await _engine.EnableAsync(new MappingConfig(updated.MappingId, updated.Name,
                    updated.LocalPort, updated.Proto, updated.TargetAddr, updated.TargetPort, peer.DeviceId));
            }
            await _store.SaveAsync(ct);
            return updated;
        }
        finally { _mutex.Release(); }
    }

    /// <summary>删除：拆监听 → 0x61（服务端幂等）→ 摘表落盘。</summary>
    public async Task DeleteAsync(Guid mappingId, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            Find(mappingId);
            await _engine.DisableAsync(mappingId);
            await _control.SendRequestAsync<MappingDeleteAck>(new MappingDelete(
                _control.NextSeq(), _control.TimestampMs(), MsgType.MappingDelete, mappingId), ct);
            _store.State.Mappings.RemoveAll(m => m.MappingId == mappingId);
            await _store.SaveAsync(ct);
        }
        finally { _mutex.Release(); }
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    private sealed record CleanMapping(string Name, ushort LocalPort, string Proto,
        string TargetRemoteCode, string TargetAddr, ushort TargetPort);

    /// <summary>字段校验（PRD 06 §2；与服务端 MappingService 同口径）。</summary>
    private static CleanMapping Validate(string name, ushort localPort, string targetRemoteCode,
        ushort targetPort, string proto, string targetAddr)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 64)
            throw new MappingException(ErrorCode.BadRequest, "名称长度须为 1~64 字符");
        if (localPort == 0 || targetPort == 0)
            throw new MappingException(ErrorCode.BadRequest, "本地/目标端口不可为 0");
        if (proto != "tcp" && proto != "udp")
            throw new MappingException(ErrorCode.BadRequest, "协议仅支持 tcp|udp");
        if (string.IsNullOrWhiteSpace(targetRemoteCode))
            throw new MappingException(ErrorCode.BadRequest, "目标远程码不可为空");
        if (string.IsNullOrWhiteSpace(targetAddr))
            throw new MappingException(ErrorCode.BadRequest, "目标地址不可为空");
        return new CleanMapping(name, localPort, proto, targetRemoteCode.Trim(),
            targetAddr.Trim(), targetPort);
    }

    /// <summary>RemoteCode → 可见设备项（0x40 分页遍历；不可见/不存在 → 4003）。</summary>
    private async Task<DeviceListItem> ResolvePeerAsync(string remoteCode, CancellationToken ct)
    {
        uint offset = 0;
        while (true)
        {
            var resp = await _control.SendRequestAsync<DeviceListResponse>(new DeviceListRequest(
                _control.NextSeq(), _control.TimestampMs(), MsgType.DeviceList, offset, 100), ct);
            var hit = resp.Items.FirstOrDefault(i => i.RemoteCode == remoteCode);
            if (hit is not null) return hit;
            if (!resp.HasMore) break;
            offset += (uint)resp.Items.Length;
        }
        throw new MappingException(ErrorCode.RemoteCodeInvalid, $"目标远程码 {remoteCode} 不存在或不可见");
    }

    private Task<MappingUpsertAck> UpsertServerAsync(StoredMapping m, bool enabled, CancellationToken ct)
        => _control.SendRequestAsync<MappingUpsertAck>(new MappingUpsert(
            _control.NextSeq(), _control.TimestampMs(), MsgType.MappingUpsert,
            m.MappingId, m.Name, m.LocalPort, m.Proto, m.TargetRemoteCode,
            m.TargetAddr, m.TargetPort, enabled), ct);

    private StoredMapping Find(Guid mappingId)
        => _store.State.Mappings.FirstOrDefault(m => m.MappingId == mappingId)
           ?? throw new MappingException(ErrorCode.NotFound, "映射不存在");

    private void UpdateStored(StoredMapping m, bool enabled)
        => ReplaceStored(m with { Enabled = enabled });

    private void ReplaceStored(StoredMapping updated)
    {
        var idx = _store.State.Mappings.FindIndex(x => x.MappingId == updated.MappingId);
        if (idx >= 0) _store.State.Mappings[idx] = updated;
    }
}

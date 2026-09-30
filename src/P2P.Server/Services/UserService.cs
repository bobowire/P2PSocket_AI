using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 用户与能力模式处理（02 §2.4 0x20/0x21/0x22/0x23/0x13；FR-S-201/202/205/106）：
/// 注册受 registration_open 开关控制；登录绑定设备 owner 并切 normal；
/// 登出切 passive（能力模式为会话态，05 §8）+ 本人映射 0x75(logged_out) 失效（M2-12，PRD 05 §4 L1 失效）；
/// 改密（M2-27）：旧密码校验 → 覆写 hash（不裁会话——能力模式不变，05 §3）。
/// </summary>
public sealed class UserService(
    IDbContextFactory<AppDbContext> dbFactory,
    AuditLogger audit,
    TimeProvider? time = null,
    InvalidationPusher? invalidation = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>0x20 用户注册：registration_open=0 → 2004；用户名占用 → Ack Ok=false。</summary>
    public async Task HandleUserRegisterAsync(ControlSession session, UserRegister msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!new ServerConfigStore(db).GetBool("registration_open"))
        {
            await session.SendErrorAsync(ErrorCode.RegistrationClosed, "registration_closed");
            return;
        }
        if (string.IsNullOrWhiteSpace(msg.Username) || msg.Password.Length < 6)
        {
            await session.SendAsync(new UserRegisterAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.UserRegister, false));
            return;
        }
        if (await db.Users.AsNoTracking().AnyAsync(u => u.Username == msg.Username))
        {
            await session.SendAsync(new UserRegisterAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.UserRegister, false));
            return;
        }

        var now = _time.GetLocalNow().UtcDateTime;
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = msg.Username,
            PasswordHash = PasswordHasher.Hash(msg.Password),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        await session.SendAsync(new UserRegisterAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.UserRegister, true));
    }

    /// <summary>0x21 登录：凭据校验 → 设备 owner 绑定 + 能力切 normal + token（FR-S-106）。</summary>
    public async Task HandleLoginAsync(ControlSession session, UserLogin msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Username == msg.Username);
        var ok = user is not null && !user.Disabled
            && PasswordHasher.Verify(msg.Password, user.PasswordHash);
        if (!ok)
        {
            await audit.WriteAsync("login_failed", session.DeviceId, detail: new { msg.Username });
            await session.SendAsync(new UserLoginAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.UserLogin, false, CapabilityMode.Passive, ""));
            return;
        }

        await db.Devices.Where(d => d.Id == session.DeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, user!.Id));
        session.Capability = CapabilityMode.Normal;
        session.OwnerUserId = user!.Id;

        var token = Convert.ToHexString(RandomGenerator.Bytes(16)).ToLowerInvariant();
        await audit.WriteAsync("login", session.DeviceId, user.Id);
        await session.SendAsync(new UserLoginAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.UserLogin, true, CapabilityMode.Normal, token));
    }

    /// <summary>0x22 登出：能力切 passive（FR-C-603；owner 绑定保留，下次登录免重绑）。</summary>
    public async Task HandleLogoutAsync(ControlSession session, UserLogout msg)
    {
        session.Capability = CapabilityMode.Passive;
        session.OwnerUserId = null;
        await session.SendAsync(new UserLogoutAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.UserLogout, true));
        // L1 失效（PRD 05 §4）：本人 enabled 映射全部失效停转发——Ack 先于推送（M2-10 纪律）
        if (invalidation is not null)
            await invalidation.PushOwnedAsync(session.DeviceId, InvalidationReason.LoggedOut);
    }

    /// <summary>0x23 修改自己密码（M2-27，FR-S-205；主动类——passive 拒 2002）：
    /// 旧密码校验失败/未登录 → Ack Ok=false（不泄漏具体原因）；成功覆写 hash 并审计。</summary>
    public async Task HandleChangePasswordAsync(ControlSession session, UserChangePassword msg)
    {
        if (session.OwnerUserId is not { } userId)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.Disabled
            || string.IsNullOrEmpty(msg.OldPassword)
            || msg.NewPassword.Length < 6
            || !PasswordHasher.Verify(msg.OldPassword, user.PasswordHash))
        {
            await audit.WriteAsync("password_change_failed", session.DeviceId, userId: userId);
            await session.SendAsync(new UserChangePasswordAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.UserChangePassword, false));
            return;
        }
        user.PasswordHash = PasswordHasher.Hash(msg.NewPassword);
        user.UpdatedAt = _time.GetLocalNow().UtcDateTime;
        await db.SaveChangesAsync();
        await audit.WriteAsync("password_change", session.DeviceId, userId: userId);
        await session.SendAsync(new UserChangePasswordAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.UserChangePassword, true));
    }

    /// <summary>0x13 设备改名（本机管理类，passive 允许——02 §2.5 矩阵）。</summary>
    public async Task HandleDeviceUpdateAsync(ControlSession session, DeviceUpdate msg)
    {
        if (string.IsNullOrWhiteSpace(msg.DeviceName))
        {
            await session.SendAsync(new DeviceUpdateAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.DeviceUpdate, false));
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Devices.Where(d => d.Id == session.DeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeviceName, msg.DeviceName));
        await session.SendAsync(new DeviceUpdateAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.DeviceUpdate, true));
    }
}

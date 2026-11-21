// M8-03 开机自启接收器（设计 13 §6：FR-C-901 开机自启语义的 Android 等价）：
// BOOT_COMPLETED → 拉起前台服务（数据面恢复：已注册设备自动重连+启用映射恢复）。
// 厂商 ROM 行为差异如实标注（不承诺全部生效，R6）；App 未曾启动过（无常态化权限）时
// 部分 ROM 限制自启——首次手动启动后生效。
using Android.Content;
using Android.Runtime;

namespace P2P.Client.Android;

// Name=固定 Java 包装类名与手写清单对齐（同 MainActivity 注记，防 crc* 双条目）。
[BroadcastReceiver(Name = "com.p2psocket.client.BootReceiver", Exported = true, Enabled = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted) return;
        try { P2pForegroundService.Start(context); }
        catch { /* 自启受限（ROM/权限）：下次手动启动生效（日志可查） */ }
    }
}

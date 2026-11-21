// M8-03 前台服务（设计 13 §3/§6）：第四种宿主形态——不经 Program.cs/Generic Host，直构
// ClientRuntime（Android 壳同款装配：BaseDir=应用私有目录/LoopbackMappingMode/SpaRootOverride/
// 指纹注入）。specialUse 前台服务+常驻通知（前台服务法定要求）；onDestroy→DisposeAsync（既有
// 优雅停机序：上报终刷→监听释放→控制通道关闭）。Web 宿主降级（M8-02）下数据面照常，通知在位。
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using P2P.Client.Hosting;

namespace P2P.Client.Android;

// Name=固定 Java 包装类名与手写清单对齐（同 MainActivity 注记，防 crc* 双条目）。
[Service(Name = "com.p2psocket.client.P2pForegroundService",
    Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class P2pForegroundService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "p2p-tunnel";

    // 进程内单例（Activity 重启/BOOT 接收器重复 Start 幂等）
    private static ClientRuntime? _runtime;
    private static FileLog? _log;
    private static TaskCompletionSource<string>? _uiUrl;

    /// <summary>启动前台服务（幂等：OnStartCommand 内运行时单例守卫）。</summary>
    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(P2pForegroundService));
        context.StartForegroundService(intent); // API 26+（minSdk=26 恒可用）
    }

    /// <summary>等管理页 URL 就位（运行时启动完成：Web 端口+token 就绪）；Web 降级态该任务
    /// 仍完成（URL 不可达由 WebView 错误页如实呈现，数据面照常）。</summary>
    public static Task<string> WaitUiUrlAsync() =>
        (_uiUrl ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        // 前台服务法定要求：常驻通知。类型参数随 API 分档（CA1416 守卫）：34+ 显式 specialUse
        //（与 manifest 一致）；29~33 传 0=FOREGROUND_SERVICE_TYPE_MANIFEST（取 manifest 声明值）；
        // 26~28 用两参重载（类型概念 29 才引入）
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, 0);
        else
            StartForeground(NotificationId, notification);
        if (_runtime is null)
            _ = RunAsync(this); // 首次启动装配数据面（后续 Start 幂等空转）
        return StartCommandResult.Sticky; // 被杀重启尽力拉起（厂商 ROM 差异不承诺全部生效）
    }

    public override IBinder? OnBind(Intent? intent) => null; // 非绑定式服务（Started semantics）

    /// <summary>运行时装配与启动（一次性；日志落 FileLog——/logs 页可读，设计 13 §3.3）。</summary>
    private static async Task RunAsync(Service service)
    {
        var baseDir = service.FilesDir!.AbsolutePath; // state/settings/peers/web-token/logs 全落应用私有目录
        var logsDir = Path.Combine(baseDir, "logs");
        _log = new FileLog(logsDir);
        var spaDir = SpaAssets.Ensure(service); // 首启 assets→数据目录（null=SPA 不托管仅 API）
        var tcs = _uiUrl ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var runtime = new ClientRuntime(new ClientRuntimeOptions
            {
                BaseDir = baseDir,
                LoopbackMappingMode = true, // TD-32：映射监听绑 127.0.0.1（无卡端口映射模式）
                MachineFingerprintOverride = () => AndroidFingerprint.Get(service), // TD-34：ANDROID_ID 派生
                SpaRootOverride = spaDir, // TD-35：SPA 物理目录托管（null=清单路径兜底不触发）
            });
            runtime.Log += m => _log?.Write("INF", m);
            _runtime = runtime;
            await runtime.StartAsync();
            _log?.Write("INF", $"壳运行时已启动（Web={runtime.WebPort}，SPA={(spaDir is null ? "未托管" : "已托管")}）");
            tcs.TrySetResult($"http://127.0.0.1:{runtime.WebPort}/?token={runtime.WebToken}");
        }
        catch (Exception e)
        {
            _log?.Write("ERR", $"壳运行时启动失败：{e}");
            tcs.TrySetResult("about:blank"); // 数据面全灭：WebView 空白（通知仍在，日志可查）
        }
    }

    public override void OnDestroy()
    {
        _log?.Write("INF", "壳服务销毁：优雅停机（上报终刷→监听释放→控制通道关闭）");
        try { _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* 停机收尾尽力而为 */ }
        _runtime = null;
        _uiUrl = null; // 下次启动重新就位
        base.OnDestroy();
    }

    private void CreateChannel()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "隧道服务", NotificationImportance.Low)
        {
            Description = "P2P 隧道前台服务常驻通知",
        };
        manager.CreateNotificationChannel(channel);
    }

    /// <summary>常驻通知：点击回 App（MainActivity 单例栈顶）。系统图标（自绘图标随发布迭代）。</summary>
    private Notification BuildNotification()
    {
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("P2P Socket 隧道运行中")
            .SetContentText("端口映射监听 127.0.0.1，点击打开管理页")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(open)
            .SetOngoing(true)
            .Build();
    }
}

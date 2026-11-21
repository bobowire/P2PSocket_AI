// M8-03 主界面（设计 13 §2.3/§5.3）：全屏 WebView 壳——加载 http://127.0.0.1:7100/?token=<web-token>
//（前台服务内 ClientRuntime 起 Kestrel，同源相对路径天然成立；token 首载经 sessionStorage 转存）。
// 首次启动请求 POST_NOTIFICATIONS（API 33+ 通知权限；拒绝不阻断——前台服务与通知通道仍工作）。
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Webkit;

namespace P2P.Client.Android;

// Name=固定 Java 包装类名（否则生成于 crc*.哈希包）——与手写清单的 .MainActivity（合并后
// com.p2psocket.client.MainActivity）对齐去重；不设则双 LAUNCHER 双图标且手写条目成死引用
//（M8-07 真机验收实测：ClassNotFoundException 崩溃，2026-11-21）。
[Activity(Name = "com.p2psocket.client.MainActivity",
    Label = "@string/app_name", MainLauncher = true, LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize,
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    private const int NotificationPermissionCode = 1001;
    private WebView? _webView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestNotificationPermission();
        P2pForegroundService.Start(this); // 数据面先起（幂等；已注册设备自动恢复映射）

        _webView = new WebView(this);
        _webView.Settings.JavaScriptEnabled = true;
        _webView.Settings.DomStorageEnabled = true; // token sessionStorage 转存链路（api.ts）
        SetContentView(_webView);
        LoadUiAsync();
    }

    /// <summary>等管理页 URL 就位后装载（运行时启动完成：Web 端口+token 就绪）。
    /// Web 降级态（M8-02：端口占用等）URL 拒连——WebView 呈现系统错误页，数据面照常（通知在位）。</summary>
    private async void LoadUiAsync()
    {
        var url = await P2pForegroundService.WaitUiUrlAsync();
        RunOnUiThread(() => _webView?.LoadUrl(url));
    }

    /// <summary>API 33+ 通知权限（拒绝仅通知不显，前台服务照常；不反复打扰——仅首启一次）。</summary>
    private void RequestNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
            == Permission.Granted) return;
        RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationPermissionCode);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        [GeneratedEnum] Permission[] grantResults)
    {
        // 通知权限拒绝不阻断：前台服务与数据面照常（设计 13 §6 保活边界）
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    protected override void OnDestroy()
    {
        _webView?.Dispose();
        _webView = null;
        base.OnDestroy();
        // 服务不随 Activity 销毁停：隧道持续（停止通道=系统通知栏移除前台服务/强制停止）
    }
}

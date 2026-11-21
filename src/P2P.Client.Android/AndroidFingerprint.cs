// M8-03 设备指纹（设计 13 §4.4/TD-34）：ANDROID_ID（64 位 hex，按「签名+用户」作用域——
// Android 8.0+ 卸载重装保持稳定，恰好优于机器指纹漂移场景）→ MachineFingerprintOverride 注入，
// MacCodeProvider.FromFingerprint 派生 macCode（注入优先——防 Android 6+ 枚举 MAC 全设备
// 统一 02:00:00:00:00:00 顶号，OQ-14）。恢复出厂/签名变更会换值：走既有覆盖式恢复/新注册语义。
using Android.Content;
using Android.Provider;

namespace P2P.Client.Android;

/// <summary>ANDROID_ID 指纹源（进程内缓存；空值/模拟器旧常量 → null=回落平台采集）。</summary>
public static class AndroidFingerprint
{
    // Android 模拟器历史固定值（bug 末代/genymotion 沿用）——同值全体模拟器，不作指纹源
    private const string EmulatorLegacyId = "9774d56d682e549c";

    private static string? _cached;
    private static bool _resolved;

    /// <summary>取注入指纹（null=无源，注册链回落平台采集——模拟器上有枚举 MAC 可用）。</summary>
    public static string? Get(Context context)
    {
        if (_resolved) return _cached;
        _resolved = true;
        try
        {
            var androidId = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.AndroidId);
            _cached = string.IsNullOrEmpty(androidId) || androidId == EmulatorLegacyId
                ? null
                : $"android-id:{androidId}";
        }
        catch
        {
            _cached = null; // ContentResolver 异常：无源（FromFingerprint 回落）
        }
        return _cached;
    }
}

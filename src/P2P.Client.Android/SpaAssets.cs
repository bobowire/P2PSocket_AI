// M8-03 SPA 资产拷出（设计 13 §5.1/§5.3）：APK assets 内 wwwroot（AndroidAsset 打包，csproj）
// → 首启拷至 <appData>/spa/<version>/（版本戳防半新半旧——升级安装新目录、旧目录随手清），
// ClientRuntimeOptions.SpaRootOverride 指向之（Android 打包布局不产出 staticwebassets 清单）。
using Android.Content;

namespace P2P.Client.Android;

/// <summary>APK assets → 应用数据目录的 SPA 拷出器（幂等：同版本且 index.html 指纹一致即跳过；
/// 指纹变化=同版本重装携新前端，清目录重拷）。</summary>
public static class SpaAssets
{
    private const string AssetRoot = "wwwroot";

    /// <summary>确保 SPA 物理目录就位，返回目录路径（assets 空集时返回 null——SPA 不托管仅 API）。</summary>
    public static string? Ensure(Context context)
    {
        var version = context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName ?? "0";
        var spaDir = Path.Combine(context.FilesDir!.AbsolutePath, "spa", version);
        if (Directory.Exists(spaDir))
        {
            // 同版本重装传播（2026-11-21 真机验收实测：同 versionName 重装 APK 后 assets 内
            // 新 SPA 不落地——目录存在即跳过）。以 index.html 内容为指纹：不一致=前端随
            // 同版本包更新过，清目录重拷（Kestrel 未起，无半新半旧窗口）。
            if (SameSpaStamp(context, spaDir)) return spaDir;
            Directory.Delete(spaDir, recursive: true);
        }
        var copied = CopyTree(context, AssetRoot, spaDir);
        if (!copied) return null;
        return spaDir;
    }

    /// <summary>assets 与已拷目录的 index.html 逐字节比对（缺任一侧=false 走全量重拷）。</summary>
    private static bool SameSpaStamp(Context context, string spaDir)
    {
        const string entry = "index.html";
        var diskPath = Path.Combine(spaDir, entry);
        if (!File.Exists(diskPath)) return false;
        try
        {
            using var asset = context.Assets!.Open($"{AssetRoot}/{entry}");
            using var disk = File.OpenRead(diskPath);
            if (asset.Length != disk.Length) return false;
            Span<byte> ab = stackalloc byte[8192];
            Span<byte> db = stackalloc byte[8192];
            while (true)
            {
                var an = asset.Read(ab);
                var dn = disk.Read(db);
                if (an != dn) return false;
                if (an == 0) return true;
                if (!ab[..an].SequenceEqual(db[..dn])) return false;
            }
        }
        catch { return false; } // assets 段缺失等：按需重拷
    }

    /// <summary>递归拷出（存在任一资产文件=true）；拷出成功后清理旧版本目录（保留最新一份）。</summary>
    private static bool CopyTree(Context context, string assetPath, string targetDir)
    {
        string[] children;
        try { children = context.Assets!.List(assetPath) ?? []; }
        catch (Exception) { return false; } // assets 段缺失（前端未构建的空 wwwroot）
        if (children.Length == 0) return false;

        var hasFile = false;
        foreach (var child in children)
        {
            var childAsset = $"{assetPath}/{child}";
            var childTarget = Path.Combine(targetDir, child);
            string[] grandChildren;
            try { grandChildren = context.Assets.List(childAsset) ?? []; }
            catch (Exception) { grandChildren = []; }
            if (grandChildren.Length > 0)
                hasFile |= CopyTree(context, childAsset, childTarget);
            else
            {
                Directory.CreateDirectory(targetDir);
                using var input = context.Assets.Open(childAsset);
                using var output = File.Create(childTarget);
                input.CopyTo(output);
                hasFile = true;
            }
        }
        if (hasFile) PruneOldVersions(context, targetDir);
        return hasFile;
    }

    /// <summary>清理其它版本目录（升级残留；当前目录的父 spa/ 下非当前名全删）。</summary>
    private static void PruneOldVersions(Context context, string currentDir)
    {
        try
        {
            var spaRoot = Directory.GetParent(currentDir);
            if (spaRoot is null) return;
            foreach (var dir in spaRoot.GetDirectories())
                if (!string.Equals(dir.FullName, currentDir, StringComparison.OrdinalIgnoreCase))
                    dir.Delete(recursive: true);
        }
        catch { /* 清理尽力而为：旧目录残留仅占空间 */ }
    }
}

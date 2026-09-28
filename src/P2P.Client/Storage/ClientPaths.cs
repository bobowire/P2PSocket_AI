// M1-22 客户端本地文件布局（03 §5）：Windows %ProgramData%\P2PClient\、Linux /etc/p2p-client/
// （目录 0700、state.json 0600，07 §4/SEC-23）。路径可注入：测试与 in-proc 集成（M1-35）
// 传临时目录，宿主装配（M1-30）用 <see cref="DefaultBaseDir"/>。
using System.IO;

namespace P2P.Client.Storage;

public static class ClientPaths
{
    public const string StateFileName = "state.json";
    public const string SettingsFileName = "settings.json";
    public const string PeersFileName = "peers.json"; // 目标设备级配置（M2-23，03 §5）
    public const string LanSegmentsFileName = "lan-segments.json"; // 内网段白名单（M2-11，03 §5）

    /// <summary>默认基目录（03 §5）。</summary>
    public static string DefaultBaseDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "P2PClient")
        : "/etc/p2p-client";

    /// <summary>确保基目录存在（Linux 权限 0700：root/专用服务账户，07 §4）。</summary>
    public static void EnsureBaseDir(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        Directory.CreateDirectory(dir,
            System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);
    }
}

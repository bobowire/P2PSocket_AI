// M1-22 原子文件写入（03 §5：临时文件+原子替换，避免断电/中途 kill 损坏终文件）：
// 写 .tmp（WriteThrough）→ Flush(flushToDisk) → File.Move 覆盖替换。终文件任意时刻要么旧全量、
// 要么新全量；残留 .tmp 由读取方清理（对终文件无影响）。Linux 下 .tmp 以 0600 创建（07 §4）。
using System.IO;

namespace P2P.Client.Storage;

internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

        await using (var stream = new FileStream(tmp, options))
        {
            await stream.WriteAsync(contents, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true); // 数据落盘后才替换：断电不产生「已替换但数据未落盘」
        }
        File.Move(tmp, path, overwrite: true);
    }
}

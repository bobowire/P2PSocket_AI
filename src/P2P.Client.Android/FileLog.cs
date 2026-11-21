// M8-03 壳文件日志（设计 13 §3.3：Serilog 不进壳，自写轻量文件日志——LocalWebApi /logs 页可读）：
// {BaseDir}/logs/client-yyyyMMdd.log，行格式对齐 Serilog 默认输出模板
// `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message}`（LogsApi.LinePrefixRegex 解析口径）。
namespace P2P.Client.Android;

/// <summary>轻量按日滚动文件日志（追加写、不做保留清理——量级=客户端诊断行，APK 私有目录）。</summary>
public sealed class FileLog : IDisposable
{
    private readonly string _logsDir;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _date = "";

    public FileLog(string logsDir)
    {
        _logsDir = logsDir;
        try
        {
            Directory.CreateDirectory(_logsDir);
            Open(DateTimeOffset.Now);
        }
        catch { /* 日志目录不可写：静默（诊断通道尽力而为，不拖垮数据面） */ }
    }

    /// <summary>写一行（level=INF/WRN/ERR，Serilog 三字符缩写口径；本地时间+偏移同 Serilog 渲染）。</summary>
    public void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is null) return;
                var now = DateTimeOffset.Now;
                if (now.ToString("yyyyMMdd") != _date)
                {
                    Open(now);
                    if (_writer is null) return;
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
                _writer.Flush();
            }
            catch { /* 写失败静默 */ }
        }
    }

    private void Open(DateTimeOffset now)
    {
        _writer?.Dispose();
        _date = now.ToString("yyyyMMdd");
        _writer = new StreamWriter(File.Open(
            Path.Combine(_logsDir, $"client-{_date}.log"),
            FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}

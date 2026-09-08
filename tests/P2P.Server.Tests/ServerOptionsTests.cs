using P2P.Server;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// 服务端配置启动校验测试（08 §5.1、NFR-35；完成判定：非法配置启动即失败且诊断可读——字段名+范围+建议）。
/// </summary>
public sealed class ServerOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    => Assert.Empty(new ServerOptions().Validate());

    [Fact]
    public void PortOutOfRange_ReportsFieldRangeAndSuggestion()
    {
        var options = new ServerOptions();
        options.Listen.Control = 70000;

        var errors = options.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("listen.control", error);         // 字段名
        Assert.Contains("70000", error);                  // 当前值
        Assert.Contains("1~65535", error);                // 合法范围
        Assert.Contains("建议", error);                    // 修复建议
    }

    [Fact]
    public void MultipleErrors_AllCollectedAtOnce()
    {
        var options = new ServerOptions();
        options.Listen.StunUdp = 0;
        options.Punch.DefaultConcurrency = 9;
        options.Logging.Level = "verbose-extra";

        var errors = options.Validate();

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("listen.stunUdp"));
        Assert.Contains(errors, e => e.Contains("punch.defaultConcurrency") && e.Contains("1~5"));
        Assert.Contains(errors, e => e.Contains("logging.level"));
    }

    [Fact]
    public void WebBind_MustBeIpAddress()
    {
        var options = new ServerOptions();
        options.Listen.WebBind = "not-an-ip";

        var error = Assert.Single(options.Validate());
        Assert.Contains("listen.webBind", error);
    }

    [Fact]
    public void EmptyDatabasePath_Rejected()
    {
        var options = new ServerOptions();
        options.Database.Path = "  ";

        var error = Assert.Single(options.Validate());
        Assert.Contains("database.path", error);
    }

    [Fact]
    public void RangeBoundaries_AcceptEdgesRejectBeyond()
    {
        var options = new ServerOptions();
        options.Heartbeat.TimeoutSec = 5;         // 下界
        options.Punch.TimeoutSec = 60;            // 上界
        options.Relay.IdleTimeoutSec = 1;
        options.Logging.RetentionDays = 365;
        Assert.Empty(options.Validate());

        options.Heartbeat.TimeoutSec = 4;         // 越下界
        options.Punch.TimeoutSec = 61;            // 越上界
        options.Relay.IdleTimeoutSec = 0;
        options.Logging.RetentionDays = 366;
        Assert.Equal(4, options.Validate().Count);
    }

    [Fact]
    public void RelayPorts_EachValidated()
    {
        var options = new ServerOptions();
        options.Listen.RelayPorts = [7010, 0, 7020];

        var error = Assert.Single(options.Validate());
        Assert.Contains("listen.relayPorts", error);
    }
}

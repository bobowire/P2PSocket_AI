// M3-15 stun-test 判型纯函数单测（05 §7.2 两桶族口径——编排与双地址世界判型归集成测）。
using System.Net;
using P2P.Core.Stun;
using P2P.Client.Diagnostics;
using Xunit;

namespace P2P.Client.Tests;

public class StunTesterTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Parse("203.0.113.10"), port);

    [Fact]
    public void ClassifyMapping_SameEndpoint_Eim()
    {
        Assert.Equal("eim", StunTester.ClassifyMapping(Ep(4000), Ep(4000)));
    }

    [Fact]
    public void ClassifyMapping_DifferentEndpoint_AdmOrApdm()
    {
        Assert.Equal("adm_or_apdm", StunTester.ClassifyMapping(Ep(4000), Ep(4001)));
    }

    [Theory]
    [InlineData(4000, null)]
    [InlineData(null, 4001)]
    public void ClassifyMapping_MissingProbe_Null(int? p1, int? p2)
    {
        Assert.Null(StunTester.ClassifyMapping(
            p1 is null ? null : Ep(p1.Value), p2 is null ? null : Ep(p2.Value)));
    }

    [Fact]
    public void ClassifyFiltering_NoChangeResponse_AdfOrApdf()
    {
        // CHANGE-REQUEST 全部尝试耗尽（超时本身即判据）：Restricted 族
        Assert.Equal("adf_or_apdf", StunTester.ClassifyFiltering(null, Ep(3479)));
    }

    [Fact]
    public void ClassifyFiltering_OriginMatchesLearnedAlt_Eif()
    {
        var alt = Ep(3479);
        var change = new StunCodec.BindingResponse(StunCodec.NewTransactionId(), Ep(4000),
            OtherAddress: Ep(3478), ResponseOrigin: alt);
        Assert.Equal("eif", StunTester.ClassifyFiltering(change, alt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3478)]
    public void ClassifyFiltering_OriginMismatch_Null(int? originPort)
    {
        // 服务端忽略 CHANGE-REQUEST（回包源仍是主端点/无属性）：不可判，防假阳性 EIF
        var alt = Ep(3479);
        var change = new StunCodec.BindingResponse(StunCodec.NewTransactionId(), Ep(4000),
            OtherAddress: Ep(3478), ResponseOrigin: originPort is null ? null : Ep(originPort.Value));
        Assert.Null(StunTester.ClassifyFiltering(change, alt));
    }
}

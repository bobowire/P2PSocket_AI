// M1-29 本地 API 运行态上下文（04 §2.1/§2.2/§2.3）：
// 宿主（M1-30）与端点模块共享的可变展示态：向导进行中标志、当前登录账号、最近注册结果。
// 真相源仍是 StateStore/ControlClient/ClientRegistrationService——此处只补"不在任何存储里"的过渡字段。
namespace P2P.Client.Web;

/// <summary>本地 API 过渡态（phase 计算输入 + 向导结果缓存）。</summary>
public sealed class LocalApiContext
{
    /// <summary>向导注册执行中（04 §2.1 phase=wizard 的判定输入）。</summary>
    public volatile bool WizardInProgress;

    /// <summary>当前登录账号（/api/device、/api/auth/me 展示；登录/登出端点维护）。</summary>
    public string? LoginUser;

    /// <summary>最近一次注册结果（04 §2.2 GET /api/wizard/result）。</summary>
    public P2P.Client.Registration.RegistrationResult? LastRegistration;
}

using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using Xunit;

namespace CloudFlow.Core.Tests.Operations;

/// <summary>
/// 提交时的认证上下文注入（设计文档 §31）。
/// 这里守的是写操作的真实性：身份若填错，真实 ARM 调用会打到错的租户，或被校验直接拒掉。
/// </summary>
public class OperationRequestFactoryTests
{
    private const string SubId = "11111111-1111-1111-1111-111111111111";
    private const string ResourceId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/VM01";

    private static ScopeContext ScopeWith(CloudAccount? account, params SubscriptionProfile[] subs)
    {
        var scope = new ScopeContext();
        scope.SetAvailableSubscriptions(subs);
        scope.SetActiveAccount(account);
        return scope;
    }

    private static CloudAccount Account(
        string? homeTenantId, string? profileId = "profile-1", string displayName = "") => new()
    {
        AccountId = "acct-1",
        Username = "user@contoso.com",
        DisplayName = displayName,
        ProviderType = AuthenticationProviderType.EmbeddedAzureCli,
        ProviderProfileId = profileId,
        HomeTenantId = homeTenantId
    };

    [Fact]
    public void 未登录_回退演示身份且不带Provider()
    {
        var factory = new OperationRequestFactory(ScopeWith(null));

        var request = factory.Create("vm.start", SubId, ResourceId, "启动虚拟机 VM01");

        Assert.Equal(DemoIdentity.AccountId, request.AccountId);
        Assert.Equal(DemoIdentity.TenantId, request.TenantId);
        Assert.Null(request.ProviderType);
        Assert.Null(request.ProviderProfileId);
    }

    [Fact]
    public void 已登录_带上真实身份三要素与Provider()
    {
        var account = Account(homeTenantId: "home-tenant");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = SubId, TenantId = "home-tenant" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("acct-1", request.AccountId);
        Assert.Equal("home-tenant", request.TenantId);
        Assert.Equal(AuthenticationProviderType.EmbeddedAzureCli, request.ProviderType);
        Assert.Equal("profile-1", request.ProviderProfileId);
    }

    [Fact]
    public void 客户租户订阅_租户取订阅登记的租户而非账户主租户()
    {
        // 账户被邀请进客户租户后：HomeTenantId 是它自己的租户，
        // 用 HomeTenantId 为该订阅取 Token 会 401 —— 租户必须跟着订阅走。
        var account = Account(homeTenantId: "contoso-tenant");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = SubId, TenantId = "customer-tenant" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("customer-tenant", request.TenantId);
    }

    [Fact]
    public void 订阅未登记_回退账户主租户()
    {
        var account = Account(homeTenantId: "contoso-tenant");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = "other-sub", TenantId = "other-tenant" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("contoso-tenant", request.TenantId);
    }

    [Fact]
    public void 订阅登记的租户为空_回退账户主租户()
    {
        var account = Account(homeTenantId: "contoso-tenant");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = SubId, TenantId = "" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("contoso-tenant", request.TenantId);
    }

    [Fact]
    public void 已登录但无主租户且订阅未登记_租户为空而非演示租户()
    {
        // 宁可让 Validate 报“缺少租户”，也不能拿演示租户去取真实 Token —— 那会静默打到错的地方。
        var account = Account(homeTenantId: null);
        var scope = ScopeWith(account);

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("", request.TenantId);
    }

    [Fact]
    public void 风险与负载_原样传递()
    {
        var payload = new Dictionary<string, string> { ["port"] = "8443" };

        var request = new OperationRequestFactory(ScopeWith(null)).Create(
            "network.open_port", SubId, ResourceId, "打开端口 8443",
            risk: RiskLevel.High, preApproved: false, payload: payload);

        Assert.Equal(RiskLevel.High, request.Risk);
        Assert.False(request.PreApproved);
        Assert.Same(payload, request.Payload);
    }

    [Fact]
    public void 未传负载_为非空空字典()
    {
        // OperationRequest.Payload 声明为不可空：Handler 一律直接索引，不需要到处判 null。
        var request = new OperationRequestFactory(ScopeWith(null))
            .Create("vm.start", SubId, ResourceId, "启动");

        Assert.NotNull(request.Payload);
        Assert.Empty(request.Payload);
    }

    [Theory]
    [InlineData("", SubId, ResourceId)]
    [InlineData("vm.start", "", ResourceId)]
    [InlineData("vm.start", SubId, "")]
    public void 缺少必要参数_立即抛错(string op, string sub, string res)
    {
        var factory = new OperationRequestFactory(ScopeWith(null));

        Assert.Throws<ArgumentException>(() => { factory.Create(op, sub, res, "启动"); });
    }

    // 任务列表的「账户」列取自 Job.AccountDisplayName。它为空就会永远显示 "—"，
    // 审计上看不出是谁执行的，所以这里守住"一定有可读名称"。

    [Fact]
    public void 已登录_带账户可读名称()
    {
        var account = Account(homeTenantId: "home-tenant", displayName: "Piao Hongji");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = SubId, TenantId = "home-tenant" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("Piao Hongji", request.AccountDisplayName);
    }

    [Fact]
    public void 已登录但无显示名_回退UPN而不是空串()
    {
        var account = Account(homeTenantId: "home-tenant");
        var scope = ScopeWith(account, new SubscriptionProfile { SubscriptionId = SubId, TenantId = "home-tenant" });

        var request = new OperationRequestFactory(scope).Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("user@contoso.com", request.AccountDisplayName);
    }

    [Fact]
    public void 未登录_账户名称标明是演示数据()
    {
        var request = new OperationRequestFactory(ScopeWith(null))
            .Create("vm.start", SubId, ResourceId, "启动");

        Assert.Equal("演示账户", request.AccountDisplayName);
    }
}

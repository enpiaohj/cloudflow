using System.IO;
using CloudFlow.Terminal.Ssh;
using CloudFlow.Terminal.Security;
using Xunit;

namespace CloudFlow.Terminal.Tests.Ssh;

/// <summary>
/// 批量连接编排。用假的 <see cref="ISshSessionHost"/> + 真的 <see cref="KnownHostStore"/>
/// （临时目录），所以既能验证编排逻辑，也能把"用户同意之前一个指纹都不写"这条安全不变量钉死。
/// </summary>
public sealed class SshBatchConnectTests
{
    // ── 夹具 ──────────────────────────────────────────────────────

    /// <summary>假适配器：模拟握手出示指纹、记录调用顺序、统计并发。</summary>
    private sealed class FakeHost : ISshSessionHost
    {
        /// <summary>让断言显得"像秘密"的文本；用来证明它不会出现在面向用户的文案里。</summary>
        public const string SecretLookingText = "password=hunter2";

        private int _concurrentConnects;

        /// <summary>各主机在握手中出示的指纹。</summary>
        public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>各目标在连接轮返回的失败码。</summary>
        public Dictionary<string, SshConnectionErrorCode> FailureCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> LiveTabs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Log { get; } = [];

        public List<string> Activated { get; } = [];

        /// <summary>连接轮实际用到的策略（用于断言"失败即拒"）。</summary>
        public List<SshHostKeyPolicy> ConnectPolicies { get; } = [];

        public int MaxConcurrentConnects { get; private set; }

        /// <summary>让该目标的连接轮抛异常。</summary>
        public string? ThrowOn { get; set; }

        public bool HasLiveTabFor(string vmResourceId) => LiveTabs.Contains(vmResourceId);

        public bool ActivateExisting(string vmResourceId)
        {
            Activated.Add(vmResourceId);
            return true;
        }

        public async Task ProbeHostKeyAsync(
            SshBatchTarget target, SshConnectionOptions options, CancellationToken ct)
        {
            Log.Add($"probe:{target.DisplayName}");
            await Task.Yield();

            // 模拟 SSH.NET 的 HostKeyReceived：出示指纹 → 交给调用方传进来的策略决定
            await options.HostKeyPolicy.ConfirmAndRememberAsync(new SshHostKey
            {
                Host = target.Host,
                Port = options.Port,
                KeyAlgorithm = "ssh-ed25519",
                Fingerprint = Fingerprints.TryGetValue(target.Host, out var value) ? value : "SHA256:unset"
            }, ct);
        }

        public async Task<SshSessionOutcome> OpenAndConnectAsync(
            SshBatchTarget target, SshConnectionOptions options, CancellationToken ct)
        {
            Log.Add($"connect:{target.DisplayName}");
            ConnectPolicies.Add(options.HostKeyPolicy);

            var concurrent = Interlocked.Increment(ref _concurrentConnects);
            MaxConcurrentConnects = Math.Max(MaxConcurrentConnects, concurrent);
            try
            {
                await Task.Yield();

                if (ThrowOn == target.DisplayName)
                {
                    throw new InvalidOperationException(SecretLookingText);
                }

                return FailureCodes.TryGetValue(target.DisplayName, out var code)
                    ? new SshSessionOutcome(false, code)
                    : new SshSessionOutcome(true, SshConnectionErrorCode.None);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentConnects);
            }
        }
    }

    /// <summary>临时目录里的 store + 假适配器。store 是真的，所以"写没写盘"可以直接断言。</summary>
    private static (FakeHost Host, KnownHostStore Store) Rig()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"),
            "known-hosts.json");
        return (new FakeHost(), new KnownHostStore(path));
    }

    /// <summary>
    /// 逐台构建连接参数的工厂，与生产代码同形状：<b>策略由编排层注入，这里不自己决定</b>。
    /// 用户名 / 口令刻意是常量 —— 本套测试关心的是编排（去重、剔项、探测、串行、汇总），
    /// 不是凭据内容。
    /// </summary>
    private static SshOptionsFactory OptionsFor() => (target, policy) => new SshConnectionOptions
    {
        Host = target.Host,
        Port = target.Port,
        Username = "azureuser",
        AuthType = SshAuthType.Password,
        Password = "irrelevant-for-orchestration",
        HostKeyPolicy = policy
    };

    /// <summary>直接调假适配器时用的一份具体参数（那条路径不需要工厂这一层）。</summary>
    private static SshConnectionOptions ConcreteOptions(SshBatchTarget target, KnownHostStore store) =>
        OptionsFor()(target, new SshHostKeyPolicy(store, _ => Task.FromResult(false)));

    private static SshBatchTarget Target(string name, bool linux = true, string? host = null) =>
        new($"/subscriptions/s/virtualMachines/{name}", name, host ?? $"{name}.example.com", linux);

    private static async Task SeedKnownAsync(KnownHostStore store, string host, string fingerprint) =>
        await store.AddAsync(new KnownHostEntry
        {
            Host = host,
            Port = 22,
            KeyAlgorithm = "ssh-ed25519",
            Fingerprint = fingerprint,
            AddedAt = DateTimeOffset.Now
        });

    // ── 安全不变量 ────────────────────────────────────────────────

    [Fact]
    public async Task 用户拒绝确认_整批不连且一个指纹都不写()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.Fingerprints["b.example.com"] = "SHA256:b";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(false));

        var report = await connector.RunAsync([Target("a"), Target("b")], OptionsFor());

        Assert.DoesNotContain(host.Log, entry => entry.StartsWith("connect:", StringComparison.Ordinal));
        Assert.Null(await store.FindAsync("a.example.com", 22));
        Assert.Null(await store.FindAsync("b.example.com", 22));
        Assert.All(report.Items, item => Assert.Equal(SshBatchItemState.Skipped, item.State));
    }

    [Fact]
    public async Task 进入确认之前一个指纹都还没写()
    {
        // 关键时序：探测轮跑完了，但用户还没表态 —— 此刻磁盘上必须什么都没有
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";

        KnownHostEntry? atConfirmTime = null;
        var connector = new SshBatchConnector(store, host, async (_, ct) =>
        {
            atConfirmTime = await store.FindAsync("a.example.com", 22, ct);
            return true;
        });

        await connector.RunAsync([Target("a")], OptionsFor());

        Assert.Null(atConfirmTime);
        Assert.NotNull(await store.FindAsync("a.example.com", 22));   // 同意之后才写
    }

    [Fact]
    public async Task 同意后恰好写入被接受的主机指纹()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.Fingerprints["b.example.com"] = "SHA256:b";

        IReadOnlyList<SshHostKey>? confirmed = null;
        var connector = new SshBatchConnector(store, host, (keys, _) =>
        {
            confirmed = keys;
            return Task.FromResult(true);
        });

        await connector.RunAsync([Target("a"), Target("b")], OptionsFor());

        Assert.Equal(2, confirmed!.Count);
        Assert.Equal("SHA256:a", (await store.FindAsync("a.example.com", 22))!.Fingerprint);
        Assert.Equal("SHA256:b", (await store.FindAsync("b.example.com", 22))!.Fingerprint);
    }

    [Fact]
    public async Task 连接轮使用失败即拒的策略()
    {
        // 若连接轮用了交互式策略，用户刚确认过的指纹会被再问一遍；
        // 用恒 false 则已被接受的指纹已在 store 里、回调根本不触发。
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        await connector.RunAsync([Target("a")], OptionsFor());

        var policy = Assert.Single(host.ConnectPolicies);

        var accepted = await policy.ConfirmAndRememberAsync(new SshHostKey
        {
            Host = "surprise.example.com",
            Port = 22,
            KeyAlgorithm = "ssh-ed25519",
            Fingerprint = "SHA256:surprise"
        });

        Assert.False(accepted);
        Assert.Null(await store.FindAsync("surprise.example.com", 22));
    }

    [Fact]
    public async Task 指纹变化的目标失败且说明批量不处理此项()
    {
        var (host, store) = Rig();
        await SeedKnownAsync(store, "a.example.com", "SHA256:old");
        host.FailureCodes["a"] = SshConnectionErrorCode.HostKeyMismatch;

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));

        var report = await connector.RunAsync([Target("a")], OptionsFor());

        var item = Assert.Single(report.Items);
        Assert.Equal(SshBatchItemState.Failed, item.State);
        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch, item.ErrorCode);
        Assert.Contains("中间人", item.Message);
        Assert.Contains("单独连接", item.Message);
        Assert.True(report.HasFailure);
    }

    [Fact]
    public async Task 失败文案不得来自异常消息()
    {
        var (host, store) = Rig();
        host.ThrowOn = "a";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        var report = await connector.RunAsync([Target("a")], OptionsFor());

        var item = Assert.Single(report.Items);
        Assert.Equal(SshBatchItemState.Failed, item.State);
        Assert.DoesNotContain(FakeHost.SecretLookingText, item.Message);
        Assert.DoesNotContain(FakeHost.SecretLookingText, report.Summary);
    }

    // ── 编排行为 ──────────────────────────────────────────────────

    [Fact]
    public async Task 连接是串行的()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.Fingerprints["b.example.com"] = "SHA256:b";
        host.Fingerprints["c.example.com"] = "SHA256:c";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        await connector.RunAsync([Target("a"), Target("b"), Target("c")], OptionsFor());

        Assert.Equal(1, host.MaxConcurrentConnects);
        Assert.Equal(
            ["connect:a", "connect:b", "connect:c"],
            host.Log.Where(entry => entry.StartsWith("connect:", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 保护"串行"那条断言的<b>仪器</b>：<see cref="FakeHost.MaxConcurrentConnects"/> 必须在真的并发时
    /// 大于 1，否则 <see cref="连接是串行的"/> 可能只是恒等于 1 而毫无鉴别力。
    /// （同仓的"反向保护"惯例：先证明测量手段有效，再相信它的读数。）
    /// </summary>
    [Fact]
    public async Task 并发计数器必须能真的检测出并发()
    {
        var (host, store) = Rig();

        var first = host.OpenAndConnectAsync(Target("a"), ConcreteOptions(Target("a"), store), default);
        var second = host.OpenAndConnectAsync(Target("b"), ConcreteOptions(Target("b"), store), default);
        await Task.WhenAll(first, second);

        Assert.True(host.MaxConcurrentConnects > 1,
            "并发计数器测不出并发，说明「连接是串行的」那条断言没有鉴别力。");
    }

    [Fact]
    public async Task 顺序保持且探测全部先于连接()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.Fingerprints["b.example.com"] = "SHA256:b";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        await connector.RunAsync([Target("a"), Target("b")], OptionsFor());

        Assert.Equal(["probe:a", "probe:b", "connect:a", "connect:b"], host.Log);
    }

    [Fact]
    public async Task 非Linux目标被跳过且不建会话()
    {
        var (host, store) = Rig();
        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));

        var report = await connector.RunAsync([Target("win", linux: false)], OptionsFor());

        var item = Assert.Single(report.Items);
        Assert.Equal(SshBatchItemState.Skipped, item.State);
        Assert.Contains("Linux", item.Message);
        Assert.Empty(host.Log);
        Assert.Contains("跳过 1 台", report.Summary);
        Assert.Contains("win", report.Summary);       // 跳过也要说清是哪台、为什么
    }

    [Fact]
    public async Task 没有IP的目标被跳过()
    {
        var (host, store) = Rig();
        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));

        var report = await connector.RunAsync([Target("a", host: "   ")], OptionsFor());

        Assert.Contains("IP", Assert.Single(report.Items).Message);
        Assert.Empty(host.Log);
    }

    [Fact]
    public async Task 重复的ResourceId只处理一次()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        var report = await connector.RunAsync([Target("a"), Target("a")], OptionsFor());

        // 重复项会让面板走"替换会话"路径、把先建的那条杀掉，必须在编排层挡住
        Assert.Single(report.Items);
        Assert.Single(host.Log, entry => entry.StartsWith("connect:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 已有会话的目标只激活不重连也不探测()
    {
        var (host, store) = Rig();
        host.LiveTabs.Add("/subscriptions/s/virtualMachines/a");

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        var report = await connector.RunAsync([Target("a")], OptionsFor());

        Assert.Equal(["/subscriptions/s/virtualMachines/a"], host.Activated);
        Assert.Empty(host.Log);       // 既不探测也不连接
        Assert.Equal(SshBatchItemState.AlreadyConnected, Assert.Single(report.Items).State);
    }

    [Fact]
    public async Task 本机已有记录的主机不做探测()
    {
        var (host, store) = Rig();
        await SeedKnownAsync(store, "a.example.com", "SHA256:a");

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        await connector.RunAsync([Target("a")], OptionsFor());

        Assert.DoesNotContain("probe:a", host.Log);
        Assert.Contains("connect:a", host.Log);
    }

    [Fact]
    public async Task 全部不可连时不弹确认也不连接()
    {
        var (host, store) = Rig();
        var confirmCalls = 0;
        var connector = new SshBatchConnector(store, host, (_, _) =>
        {
            confirmCalls++;
            return Task.FromResult(true);
        });

        var report = await connector.RunAsync([Target("win", linux: false)], OptionsFor());

        Assert.Equal(0, confirmCalls);
        Assert.Empty(host.Log);
        Assert.False(report.HasFailure);
    }

    // ── 汇总文案 ──────────────────────────────────────────────────

    [Fact]
    public async Task 汇总把成功跳过失败分开计数并列出原因()
    {
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.FailureCodes["b"] = SshConnectionErrorCode.AuthenticationFailed;

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        var report = await connector.RunAsync(
            [Target("a"), Target("b"), Target("win", linux: false)], OptionsFor());

        Assert.Contains("已连接 1 台", report.Summary);
        Assert.Contains("跳过 1 台", report.Summary);
        Assert.Contains("失败 1 台", report.Summary);
        Assert.Contains("b：", report.Summary);
        Assert.Contains("认证失败", report.Summary);
        Assert.True(report.HasFailure);
    }

    [Fact]
    public async Task 汇总与结果文案不得泄露凭据明文()
    {
        // 编排层手里确实握着 SshConnectionOptions（内含明文），所以这条是实打实的检查，
        // 不是"类型里本来就没有秘密字段"那种空跑。
        var (host, store) = Rig();
        host.Fingerprints["a.example.com"] = "SHA256:a";
        host.FailureCodes["b"] = SshConnectionErrorCode.AuthenticationFailed;

        var connector = new SshBatchConnector(store, host, (_, _) => Task.FromResult(true));
        var report = await connector.RunAsync([Target("a"), Target("b")], OptionsFor());

        Assert.DoesNotContain("irrelevant-for-orchestration", report.Summary, StringComparison.Ordinal);
        Assert.All(report.Items, item =>
            Assert.DoesNotContain("irrelevant-for-orchestration", item.Message, StringComparison.Ordinal));
    }
}

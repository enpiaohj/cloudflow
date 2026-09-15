using System.IO;
using CloudFlow.Terminal.Ssh;
using CloudFlow.Terminal.Security;
using Xunit;

namespace CloudFlow.Terminal.Tests.Ssh;

/// <summary>Host Key 两段式策略：Lookup 同步比对，Confirm 由 UI 回调决定是否记住。</summary>
public sealed class SshHostKeyPolicyTests
{
    private static (SshHostKeyPolicy Policy, KnownHostStore Store) Build(
        Func<SshHostKey, Task<bool>>? confirm = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"),
            "known-hosts.json");
        var store = new KnownHostStore(path);
        return (new SshHostKeyPolicy(store, confirm ?? (_ => Task.FromResult(true))), store);
    }

    [Fact]
    public async Task 首次连接_未记录_需要确认且不是不匹配()
    {
        var (policy, _) = Build();

        var key = policy.Lookup("52.141.44.28", 22, "ssh-ed25519", "SHA256:new");

        Assert.False(key.IsKnownGood);
        Assert.False(key.IsMismatch);
        Assert.Null(key.KnownFingerprint);
    }

    [Fact]
    public async Task 指纹一致_放行()
    {
        var (policy, store) = Build();
        await store.AddAsync(new KnownHostEntry
        {
            Host = "52.141.44.28", Port = 22, KeyAlgorithm = "ssh-ed25519",
            Fingerprint = "SHA256:same", AddedAt = DateTimeOffset.Now
        });

        var key = policy.Lookup("52.141.44.28", 22, "ssh-ed25519", "SHA256:same");

        Assert.True(key.IsKnownGood);
        Assert.False(key.IsMismatch);
    }

    [Fact]
    public async Task 指纹不一致_强警告标记()
    {
        var (policy, store) = Build();
        await store.AddAsync(new KnownHostEntry
        {
            Host = "52.141.44.28", Port = 22, KeyAlgorithm = "ssh-ed25519",
            Fingerprint = "SHA256:old", AddedAt = DateTimeOffset.Now
        });

        var key = policy.Lookup("52.141.44.28", 22, "ssh-ed25519", "SHA256:changed");

        Assert.False(key.IsKnownGood);
        Assert.True(key.IsMismatch);
    }

    [Fact]
    public async Task 用户接受_记录指纹()
    {
        var confirmCalls = 0;
        var (policy, store) = Build(_ => { confirmCalls++; return Task.FromResult(true); });

        var key = policy.Lookup("52.141.44.28", 22, "ssh-ed25519", "SHA256:new");
        var accepted = await policy.ConfirmAndRememberAsync(key);

        Assert.True(accepted);
        Assert.Equal(1, confirmCalls);
        Assert.Equal("SHA256:new", (await store.FindAsync("52.141.44.28", 22))!.Fingerprint);
    }

    [Fact]
    public async Task 用户拒绝_不记录()
    {
        var (policy, store) = Build(_ => Task.FromResult(false));

        var key = policy.Lookup("52.141.44.28", 22, "ssh-ed25519", "SHA256:new");
        var accepted = await policy.ConfirmAndRememberAsync(key);

        Assert.False(accepted);
        Assert.Null(await store.FindAsync("52.141.44.28", 22));
    }
}

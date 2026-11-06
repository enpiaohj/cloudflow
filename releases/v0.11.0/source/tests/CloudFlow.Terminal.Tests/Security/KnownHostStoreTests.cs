using System.IO;
using CloudFlow.Terminal.Security;
using Xunit;

namespace CloudFlow.Terminal.Tests.Security;

/// <summary>SSH 主机指纹记录：首次确认后记录，同主机端口覆盖更新。</summary>
public sealed class KnownHostStoreTests
{
    private static (KnownHostStore Store, string Path) Build()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfterminal-tests", Guid.NewGuid().ToString("N"),
            "known-hosts.json");
        return (new KnownHostStore(path), path);
    }

    private static KnownHostEntry Entry(string host = "52.141.44.28", string fp = "SHA256:aaa") => new()
    {
        Host = host,
        Port = 22,
        KeyAlgorithm = "ssh-ed25519",
        Fingerprint = fp,
        AddedAt = DateTimeOffset.Now
    };

    [Fact]
    public async Task 首次记录后按主机端口查到()
    {
        var (store, _) = Build();

        await store.AddAsync(Entry());

        var found = await store.FindAsync("52.141.44.28", 22);
        Assert.NotNull(found);
        Assert.Equal("SHA256:aaa", found.Fingerprint);
    }

    [Fact]
    public async Task 未记录的主机返回null()
    {
        var (store, _) = Build();

        Assert.Null(await store.FindAsync("1.2.3.4", 22));
    }

    [Fact]
    public async Task 同主机端口重连_覆盖指纹()
    {
        var (store, _) = Build();
        await store.AddAsync(Entry());
        await store.AddAsync(Entry(fp: "SHA256:bbb"));

        var found = await store.FindAsync("52.141.44.28", 22);
        Assert.Equal("SHA256:bbb", found!.Fingerprint);
    }

    [Fact]
    public async Task 删除后返回null()
    {
        var (store, _) = Build();
        await store.AddAsync(Entry());

        await store.RemoveAsync("52.141.44.28", 22);

        Assert.Null(await store.FindAsync("52.141.44.28", 22));
    }
}

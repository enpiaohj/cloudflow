using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

public sealed class ActiveAccountStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "account-context.json");

    [Fact]
    public void Save_新实例可恢复活动账户Id()
    {
        var writer = new ActiveAccountStore(FilePath);
        writer.Save("account-a");

        var reader = new ActiveAccountStore(FilePath);

        Assert.Equal("account-a", reader.Load());
    }

    [Fact]
    public void Clear_移除持久化账户Id()
    {
        var store = new ActiveAccountStore(FilePath);
        store.Save("account-a");

        store.Clear();

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_损坏文件返回空值()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{");

        Assert.Null(new ActiveAccountStore(FilePath).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

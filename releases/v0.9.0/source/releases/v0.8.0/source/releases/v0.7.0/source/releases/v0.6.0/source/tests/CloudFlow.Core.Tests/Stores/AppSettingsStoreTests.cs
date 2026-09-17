using CloudFlow.Core.Operations;
using CloudFlow.Data.Stores;
using Xunit;

namespace CloudFlow.Core.Tests.Stores;

/// <summary>
/// 应用设置持久化。重点是**坏输入不致命**：设置文件是用户可以手改的，
/// 改坏了应该是"回到默认值"，不是"应用起不来" —— 起不来就没法进设置页改回去。
/// </summary>
public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void 缺文件时返回默认值且不创建文件()
    {
        var store = new AppSettingsStore(FilePath);

        Assert.Equal(AppSettings.Default, store.Current);
        // 只读不写：应用启动时读一次设置，不该因为读就凭空造出一个文件
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void 默认值是本设置存在之前的行为()
    {
        var defaults = AppSettings.Default.Normalize();

        Assert.Equal(AppThemeKind.System, defaults.Theme);
        Assert.Equal(ApprovalPolicy.HighRiskOnly, defaults.ApprovalPolicy);
        Assert.Equal(0, defaults.AutoRefreshSeconds);
        Assert.Equal(10, defaults.DefaultPageSize);
        Assert.True(defaults.AutoDetectPublicIp);
    }

    [Fact]
    public void 保存后新实例可读回全部字段()
    {
        var store = new AppSettingsStore(FilePath);
        store.Save(new AppSettings
        {
            Theme = AppThemeKind.Dark,
            ApprovalPolicy = ApprovalPolicy.AllWrites,
            AutoRefreshSeconds = 60,
            DefaultPageSize = 50,
            AutoDetectPublicIp = false
        });

        var restored = new AppSettingsStore(FilePath).Current;

        Assert.Equal(AppThemeKind.Dark, restored.Theme);
        Assert.Equal(ApprovalPolicy.AllWrites, restored.ApprovalPolicy);
        Assert.Equal(60, restored.AutoRefreshSeconds);
        Assert.Equal(50, restored.DefaultPageSize);
        Assert.False(restored.AutoDetectPublicIp);
    }

    [Fact]
    public void 枚举以字符串落盘便于人工查看与修改()
    {
        new AppSettingsStore(FilePath).Save(new AppSettings
        {
            Theme = AppThemeKind.Dark,
            ApprovalPolicy = ApprovalPolicy.Off
        });

        var json = File.ReadAllText(FilePath);

        Assert.Contains("\"Dark\"", json);
        Assert.Contains("\"Off\"", json);
    }

    [Fact]
    public void 文件损坏时回退默认值而不是抛异常()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json ");

        var store = new AppSettingsStore(FilePath);

        Assert.Equal(AppSettings.Default, store.Current);
    }

    [Fact]
    public void 文件为空时回退默认值()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "");

        Assert.Equal(AppSettings.Default, new AppSettingsStore(FilePath).Current);
    }

    [Theory]
    [InlineData(45)]     // 不在下拉框选项里
    [InlineData(-30)]    // 负数
    [InlineData(99999)]  // 过大
    public void 不支持的刷新间隔回退到关闭(int seconds)
    {
        var normalized = (AppSettings.Default with { AutoRefreshSeconds = seconds }).Normalize();

        // 回退到 0（关闭）而不是某个正值：退到"不做任何后台请求"那一档才不会
        // 在用户没要求的情况下定期打 Azure
        Assert.Equal(0, normalized.AutoRefreshSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void 支持的刷新间隔原样保留(int seconds)
    {
        var normalized = (AppSettings.Default with { AutoRefreshSeconds = seconds }).Normalize();

        Assert.Equal(seconds, normalized.AutoRefreshSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-10)]
    public void 不支持的每页条数回退到10(int size)
    {
        var normalized = (AppSettings.Default with { DefaultPageSize = size }).Normalize();

        Assert.Equal(10, normalized.DefaultPageSize);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void 支持的每页条数原样保留(int size)
    {
        var normalized = (AppSettings.Default with { DefaultPageSize = size }).Normalize();

        Assert.Equal(size, normalized.DefaultPageSize);
    }

    [Fact]
    public void 手改文件写入未知枚举名时回退默认值而不是抛异常()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath,
            """{ "Theme": "Neon", "ApprovalPolicy": "Whatever", "DefaultPageSize": 50 }""");

        var settings = new AppSettingsStore(FilePath).Current;

        // Theme / ApprovalPolicy 回退，但同一文件里合法的字段要保住 ——
        // 一处坏值不该把整份设置重置掉
        Assert.Equal(AppThemeKind.System, settings.Theme);
        Assert.Equal(ApprovalPolicy.HighRiskOnly, settings.ApprovalPolicy);
        Assert.Equal(50, settings.DefaultPageSize);
    }

    [Fact]
    public void 保存越界值时落盘的是归一化后的值()
    {
        var store = new AppSettingsStore(FilePath);

        var effective = store.Save(AppSettings.Default with { DefaultPageSize = 13 });

        Assert.Equal(10, effective.DefaultPageSize);
        // 内存与文件必须一致：文件里留着 13 会让下次启动重新归一化一遍，
        // 两次结果虽然相同，但读文件的人会以为 13 是生效值
        Assert.Equal(10, new AppSettingsStore(FilePath).Current.DefaultPageSize);
    }

    [Fact]
    public void 保存后触发Changed并给出归一化后的值()
    {
        var store = new AppSettingsStore(FilePath);
        AppSettings? observed = null;
        store.Changed += (_, settings) => observed = settings;

        store.Save(AppSettings.Default with { DefaultPageSize = 13 });

        Assert.NotNull(observed);
        Assert.Equal(10, observed!.DefaultPageSize);
    }

    [Fact]
    public void 只读不改时不会触发Changed()
    {
        var store = new AppSettingsStore(FilePath);
        var raised = false;
        store.Changed += (_, _) => raised = true;

        _ = store.Current;

        Assert.False(raised);
    }

    [Fact]
    public void 目录不存在时保存会自动创建()
    {
        // _directory 尚未创建，模拟首次运行
        var store = new AppSettingsStore(FilePath);

        store.Save(AppSettings.Default with { Theme = AppThemeKind.Light });

        Assert.True(File.Exists(FilePath));
        Assert.Equal(AppThemeKind.Light, new AppSettingsStore(FilePath).Current.Theme);
    }

    [Fact]
    public void 保存不留下临时文件()
    {
        new AppSettingsStore(FilePath).Save(AppSettings.Default);

        // 残留的 .tmp 说明 Move 没执行完，下次写入会撞上一个半截文件
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}

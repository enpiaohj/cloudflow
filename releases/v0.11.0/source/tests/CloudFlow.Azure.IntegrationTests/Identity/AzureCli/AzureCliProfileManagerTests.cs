using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// AzureCliProfileManager 行为测试（P0 Spike 步骤 3）。
/// 验证多 Profile 生命周期与隔离：创建互不相同、GUID 校验防路径穿越、
/// 枚举忽略垃圾目录、删除不影响其他 Profile、登出仅作用于目标 Profile。
/// </summary>
public sealed class AzureCliProfileManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfprofiles-" + Guid.NewGuid().ToString("N"));

    private AzureCliProfileManager CreateManager(string? azCmdPath = null) =>
        new(_runner, azCmdPath, _root);

    private readonly AzureCliProcessRunner _runner = new();

    [Fact]
    public void Create_每次生成互不相同的隔离Profile()
    {
        var manager = CreateManager();

        var idA = manager.CreateProfile();
        var idB = manager.CreateProfile();

        Assert.NotEqual(idA, idB);
        Assert.True(Guid.TryParseExact(idA, "N", out _), "ProfileId 应为 N 格式 GUID");
        Assert.True(Directory.Exists(Path.Combine(_root, "cli-profile-" + idA)));
        Assert.True(Directory.Exists(Path.Combine(_root, "cli-profile-" + idB)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("../evil")]
    [InlineData("..\\..\\Windows")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void GetProfilePath_非法ProfileId一律拒绝(string invalidId)
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.GetProfilePath(invalidId));
    }

    [Fact]
    public void GetProfileIds_仅枚举合法Profile并忽略垃圾目录()
    {
        var manager = CreateManager();
        var idA = manager.CreateProfile();
        manager.CreateProfile();
        // 模拟第三方/历史残留目录：同前缀但非 GUID、以及无关目录
        Directory.CreateDirectory(Path.Combine(_root, "cli-profile-notaguid"));
        Directory.CreateDirectory(Path.Combine(_root, "unrelated"));

        var ids = manager.GetProfileIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains(idA, ids);
    }

    [Fact]
    public void Delete_仅移除目标Profile且可重复执行()
    {
        var manager = CreateManager();
        var idA = manager.CreateProfile();
        var idB = manager.CreateProfile();

        manager.DeleteProfile(idA);
        manager.DeleteProfile(idA); // 幂等

        Assert.False(Directory.Exists(Path.Combine(_root, "cli-profile-" + idA)));
        Assert.True(Directory.Exists(Path.Combine(_root, "cli-profile-" + idB)));
    }

    [Fact]
    public async Task Logout_仅作用于目标Profile的配置目录()
    {
        var probeScript = Path.Combine(_root, "fake-az.cmd");
        Directory.CreateDirectory(_root);
        File.WriteAllText(probeScript, "@echo CONFIG=[%AZURE_CONFIG_DIR%]\r\n@exit /b 0");
        var manager = CreateManager(probeScript);
        var idA = manager.CreateProfile();
        var idB = manager.CreateProfile();

        var result = await manager.LogoutAsync(idA);

        Assert.True(result.Succeeded);
        Assert.Contains(Path.Combine(_root, "cli-profile-" + idA), result.StandardOutput);
        Assert.DoesNotContain("cli-profile-" + idB, result.StandardOutput);
    }

    [Fact]
    public async Task Logout_Runtime缺失时明确报错()
    {
        var manager = CreateManager(azCmdPath: null);
        var id = manager.CreateProfile();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.LogoutAsync(id));

        Assert.Contains("Runtime", exception.Message);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
        }
    }
}

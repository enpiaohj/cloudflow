namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// Azure CLI Profile 生命周期管理（P0 Spike 步骤 3，规范 §五）。
/// 每个个人账户一个独立 Profile 目录，互不共享 Token / 订阅 / 登录状态；
/// ProfileId 必须是 N 格式 GUID，杜绝路径穿越；删除可幂等执行且不影响其他 Profile。
/// Profile 根目录默认：%LOCALAPPDATA%\CloudFlow\Identity\cli-profile-{GUID}\
/// </summary>
public sealed class AzureCliProfileManager
{
    private const string Prefix = "cli-profile-";

    private readonly string _profilesRoot;
    private readonly AzureCliProcessRunner _runner;
    private readonly string? _azCmdPath;

    public AzureCliProfileManager(
        AzureCliProcessRunner runner,
        string? azCmdPath = null,
        string? profilesRoot = null)
    {
        _runner = runner;
        _azCmdPath = azCmdPath;
        _profilesRoot = profilesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudFlow", "Identity");
    }

    /// <summary>创建新 Profile，返回其 GUID 形态 ProfileId。</summary>
    public string CreateProfile()
    {
        var profileId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(GetValidatedPath(profileId));
        return profileId;
    }

    /// <summary>枚举现存合法 Profile；忽略非 GUID 前缀目录与无关目录，不创建根目录。</summary>
    public IReadOnlyList<string> GetProfileIds()
    {
        if (!Directory.Exists(_profilesRoot))
        {
            return [];
        }

        return [.. Directory.GetDirectories(_profilesRoot, Prefix + "*")
            .Select(Path.GetFileName)
            .Where(name => name is not null && Guid.TryParseExact(name[Prefix.Length..], "N", out _))
            .Select(name => name![Prefix.Length..])];
    }

    /// <summary>解析 Profile 完整路径；非法 ID（含路径穿越）一律拒绝。</summary>
    public string GetProfilePath(string profileId)
    {
        return GetValidatedPath(profileId);
    }

    /// <summary>删除 Profile（幂等）；其他 Profile 不受影响。</summary>
    public void DeleteProfile(string profileId)
    {
        var path = GetValidatedPath(profileId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 在指定 Profile 内执行 az logout：仅该 Profile 的登录状态被清除，
    /// 其他 Profile 的 Token / 订阅 / 登录状态不受影响。
    /// </summary>
    public async Task<AzureCliResult> LogoutAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (_azCmdPath is null)
        {
            throw new InvalidOperationException(
                "Azure CLI Runtime 不可用，无法执行登出。请通过 CloudFlow 修复 Runtime。");
        }

        var profilePath = GetValidatedPath(profileId);
        if (!Directory.Exists(profilePath))
        {
            throw new InvalidOperationException($"Profile {profileId} 不存在，无法登出。");
        }

        return await _runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = _azCmdPath,
            Arguments = ["logout"],
            ConfigDirectory = profilePath
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>唯一入口的 ID 校验：必须是 N 格式 GUID，防止 cli-profile- 前缀目录被伪造穿越。</summary>
    private string GetValidatedPath(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            !Guid.TryParseExact(profileId, "N", out _))
        {
            throw new ArgumentException(
                $"ProfileId 必须是 N 格式 GUID，收到：[{profileId}]。", nameof(profileId));
        }

        return Path.Combine(_profilesRoot, Prefix + profileId);
    }
}

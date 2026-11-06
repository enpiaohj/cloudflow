namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>Azure CLI Profile 生命周期契约（便于测试替身；实现见 AzureCliProfileManager）。</summary>
public interface IAzureCliProfileManager
{
    string CreateProfile();

    IReadOnlyList<string> GetProfileIds();

    string GetProfilePath(string profileId);

    void DeleteProfile(string profileId);

    Task<AzureCliResult> LogoutAsync(string profileId, CancellationToken cancellationToken = default);
}

namespace CloudFlow.Core.Identity;

/// <summary>
/// 个人 Microsoft 账户（Embedded Azure CLI 身份）非敏感元数据的本机注册表。
/// CLI Profile 只保存 Token，不保存 CloudAccount 模型；本注册表用于应用重启后
/// 在账户列表 / 顶栏恢复个人账户。仅允许保存 AccountId、Username、DisplayName、
/// ProviderType、ProviderProfileId；禁止保存 Token、Refresh Token、密码、Client Secret。
/// </summary>
public interface IPersonalAccountRegistry
{
    IReadOnlyList<CloudAccount> Load();

    void Save(CloudAccount account);

    void Remove(string accountId);
}

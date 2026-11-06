using System.Text.Json;
using CloudFlow.Core.Identity;

namespace CloudFlow.Data.Stores;

/// <summary>
/// 个人 Microsoft 账户（Embedded Azure CLI 身份）的非敏感元数据注册表。
/// 用途：应用重启后仍能在账户列表 / 顶栏恢复个人账户（CLI Profile 只保存 Token，
/// 不保存 CloudAccount 模型）。仅保存 AccountId / Username / DisplayName /
/// ProviderProfileId；绝不保存 Token、Refresh Token、密码或 CLI Cache 内容。
/// </summary>
public sealed class PersonalAccountStore : IPersonalAccountRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private const string PersonalAccountPrefix = "azurecli:";

    private readonly string _filePath;
    private readonly object _lock = new();

    public PersonalAccountStore(string? filePath = null)
    {
        _filePath = filePath ?? CloudFlowPaths.PersonalAccountsFile;
    }

    public IReadOnlyList<CloudAccount> Load()
    {
        lock (_lock)
        {
            var records = ReadAll();
            return [.. records
                .Where(record => record.ProviderType == AuthenticationProviderType.EmbeddedAzureCli)
                .Where(record => !string.IsNullOrWhiteSpace(record.AccountId))
                .Where(record =>
                    !string.IsNullOrWhiteSpace(record.ProviderProfileId) &&
                    string.Equals(record.AccountId, PersonalAccountPrefix + record.ProviderProfileId, StringComparison.Ordinal))
                .Select(record => new CloudAccount
                {
                    AccountId = record.AccountId,
                    Username = record.Username,
                    DisplayName = record.DisplayName,
                    ProviderType = record.ProviderType,
                    ProviderProfileId = record.ProviderProfileId
                })];
        }
    }

    public void Save(CloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        lock (_lock)
        {
            var records = ReadAll();
            records.RemoveAll(record =>
                string.Equals(record.AccountId, account.AccountId, StringComparison.Ordinal));
            records.Add(new AccountRecord
            {
                AccountId = account.AccountId,
                Username = account.Username,
                DisplayName = account.DisplayName,
                ProviderType = account.ProviderType,
                ProviderProfileId = account.ProviderProfileId
            });
            WriteAll(records);
        }
    }

    public void Remove(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        lock (_lock)
        {
            var records = ReadAll();
            var removed = records.RemoveAll(record =>
                string.Equals(record.AccountId, accountId, StringComparison.Ordinal));
            if (removed > 0)
            {
                WriteAll(records);
            }
        }
    }

    private List<AccountRecord> ReadAll()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<AccountRecord>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // 损坏文件不影响启动：视为无已保存个人账户
            return [];
        }
    }

    private void WriteAll(List<AccountRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, Options));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private sealed class AccountRecord
    {
        public string AccountId { get; set; } = "";

        public string Username { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public AuthenticationProviderType ProviderType { get; set; }

        public string? ProviderProfileId { get; set; }
    }
}

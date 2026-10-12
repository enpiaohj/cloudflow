using CloudFlow.Core.Scopes;

namespace CloudFlow.Data.Stores;

/// <summary>
/// Saved Scope 本地存储（设计文档 §10）。
/// 首次运行写入 Demo 默认值；真实 Azure 接入后由订阅发现结果替换。
/// </summary>
public sealed class SavedScopeStore : JsonFileStore<List<SavedScope>>
{
    protected override string FilePath => CloudFlowPaths.SavedScopesFile;

    public IReadOnlyList<SavedScope> LoadOrDefault()
    {
        var existing = Load();
        if (existing is { Count: > 0 })
        {
            return existing;
        }

        var defaults = CreateDemoDefaults();
        Save(defaults);
        return defaults;
    }

    public void SaveAll(IEnumerable<SavedScope> scopes) => Save([.. scopes]);

    /// <summary>Demo 默认 Saved Scope：Production / Development（与概念图一致）。</summary>
    private static List<SavedScope> CreateDemoDefaults() =>
    [
        new SavedScope
        {
            Name = "Production",
            Description = "Prod-China / Prod-Korea / Prod-US",
            SubscriptionIds =
            [
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                "33333333-3333-3333-3333-333333333333"
            ]
        },
        new SavedScope
        {
            Name = "Development",
            Description = "Dev / Test / Sandbox",
            SubscriptionIds =
            [
                "44444444-4444-4444-4444-444444444444",
                "55555555-5555-5555-5555-555555555555",
                "66666666-6666-6666-6666-666666666666"
            ]
        }
    ];
}

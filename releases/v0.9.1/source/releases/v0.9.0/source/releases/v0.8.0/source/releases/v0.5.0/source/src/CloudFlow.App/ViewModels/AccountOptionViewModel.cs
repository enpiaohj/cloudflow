using CloudFlow.Core.Identity;

namespace CloudFlow.App.ViewModels;

public sealed class AccountOptionViewModel
{
    public required CloudAccount Account { get; init; }

    /// <summary>当前生效账户（账户菜单中显示勾选）。</summary>
    public bool IsActive { get; init; }

    /// <summary>账户展示名：优先显示名，缺失时回退用户名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Account.DisplayName)
        ? Account.Username
        : Account.DisplayName;

    /// <summary>账户第二行：用户名（UPN），用于区分同名账户。</summary>
    public string Subtitle => Account.Username;

    /// <summary>头像首字母（顶栏与账户菜单共用）。</summary>
    public string Initial => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : char.ToUpperInvariant(DisplayName.Trim()[0]).ToString();

    /// <summary>单行标签（设置页账户列表等仍在使用）。</summary>
    public string Label => string.Equals(DisplayName, Subtitle, StringComparison.Ordinal)
        ? DisplayName
        : $"{DisplayName} ({Subtitle})";
}

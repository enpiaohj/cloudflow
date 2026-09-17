using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Compute.Operations;

namespace CloudFlow.App.Views;

/// <summary>
/// 创建虚拟机的两步向导：先收集目标与实例信息，再收集已有子网和认证方式。
/// </summary>
/// <remarks>
/// 密码只在 <see cref="AdminPasswordBox"/> 与返回结果的短暂内存生命周期中存在；
/// 结果的 <see cref="CreateVmResult.Parameters"/> 绝不含密码，调用方必须先写入 DPAPI 凭据库、
/// 再仅把凭据 Id 提交给 Operation Engine。
/// </remarks>
public partial class CreateVmWizardDialog : CfDialogWindow
{
    private static readonly Regex VmNamePattern = new(
        "^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}[a-zA-Z0-9]$", RegexOptions.Compiled);

    private bool _isSecondStep;
    private bool _settingCredentialSuggestion;
    private bool _credentialNameWasEdited;

    public CreateVmWizardDialog(
        IReadOnlyList<ProvisioningSubscriptionOption> subscriptions,
        IReadOnlyList<string> resourceGroups,
        IReadOnlyList<string> regions)
    {
        InitializeComponent();

        SubscriptionBox.ItemsSource = subscriptions;
        SubscriptionBox.DisplayMemberPath = nameof(ProvisioningSubscriptionOption.DisplayName);
        SubscriptionBox.SelectedValuePath = nameof(ProvisioningSubscriptionOption.SubscriptionId);
        ResourceGroupBox.ItemsSource = resourceGroups;
        RegionBox.ItemsSource = regions;

        if (subscriptions.Count == 1)
        {
            SubscriptionBox.SelectedIndex = 0;
        }

        if (resourceGroups.Count == 1)
        {
            ResourceGroupBox.SelectedIndex = 0;
        }

        if (regions.Count == 1)
        {
            RegionBox.SelectedIndex = 0;
        }

        ImageBox.SelectedIndex = 0;
        VmSizeBox.SelectedIndex = 0;
        UpdateCredentialPanels();
    }

    /// <summary>用户确认后的非持久化草稿；取消时为 <c>null</c>。</summary>
    public CreateVmResult? Result { get; private set; }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBasicStep())
        {
            return;
        }

        SetStep(secondStep: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => SetStep(secondStep: false);

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBasicStep() || !ValidateNetworkAndCredentials())
        {
            return;
        }

        var image = SelectedTag(ImageBox);
        var usePassword = PasswordAuthBox.IsChecked is true;
        var password = usePassword ? AdminPasswordBox.Password : null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CreateVmHandler.PayloadVmName] = VmNameBox.Text.Trim(),
            [CreateVmHandler.PayloadSubnetId] = SubnetIdBox.Text.Trim(),
            [CreateVmHandler.PayloadVmSize] = SelectedContent(VmSizeBox),
            [CreateVmHandler.PayloadImage] = image,
            [CreateVmHandler.PayloadAdminUsername] = AdminUsernameBox.Text.Trim(),
            [CreateVmHandler.PayloadAuthType] = usePassword ? "password" : "ssh",
            [CreateVmHandler.PayloadPublicIp] = (PublicIpBox.IsChecked is true).ToString().ToLowerInvariant()
        };

        if (usePassword)
        {
            // 密码本体永远不进入 parameters；ViewModel 会先持久化为 DPAPI 凭据，
            // 然后将 credentialId 补进此字典再交给 IVmProvisioningService。
        }
        else
        {
            parameters[CreateVmHandler.PayloadSshPublicKey] = SshPublicKeyBox.Text.Trim();
        }

        Result = new CreateVmResult(
            SelectedSubscriptionId(),
            ResourceGroupBox.Text.Trim(),
            RegionBox.Text.Trim(),
            parameters,
            password,
            usePassword ? CredentialNameBox.Text.Trim() : null);
        DialogResult = true;
    }

    private void AuthType_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateCredentialPanels();
        }
    }

    private void ImageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !IsWindowsImage())
        {
            return;
        }

        // Windows 不能用 Linux SSH public-key OS profile；直接切到唯一兼容的密码方式。
        PasswordAuthBox.IsChecked = true;
    }

    private void VmNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_credentialNameWasEdited)
        {
            SetSuggestedCredentialName();
        }
    }

    private void CredentialNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingCredentialSuggestion)
        {
            _credentialNameWasEdited = true;
        }
    }

    private void UpdateCredentialPanels()
    {
        var usePassword = PasswordAuthBox.IsChecked is true;
        SshKeyPanel.Visibility = usePassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordPanel.Visibility = usePassword ? Visibility.Visible : Visibility.Collapsed;

        if (usePassword && !_credentialNameWasEdited)
        {
            SetSuggestedCredentialName();
        }
    }

    private void SetSuggestedCredentialName()
    {
        _settingCredentialSuggestion = true;
        CredentialNameBox.Text = string.IsNullOrWhiteSpace(VmNameBox.Text)
            ? string.Empty
            : $"{VmNameBox.Text.Trim()}-admin-password";
        _settingCredentialSuggestion = false;
    }

    private void SetStep(bool secondStep)
    {
        _isSecondStep = secondStep;
        BasicPanel.Visibility = secondStep ? Visibility.Collapsed : Visibility.Visible;
        NetworkPanel.Visibility = secondStep ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = secondStep ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = secondStep ? Visibility.Collapsed : Visibility.Visible;
        CreateButton.Visibility = secondStep ? Visibility.Visible : Visibility.Collapsed;

        BasicStepBadge.Background = secondStep
            ? FindResource("Cf.PageBackgroundBrush") as System.Windows.Media.Brush
            : FindResource("Cf.PrimaryBrush") as System.Windows.Media.Brush;
        NetworkStepBadge.Background = secondStep
            ? FindResource("Cf.PrimaryBrush") as System.Windows.Media.Brush
            : FindResource("Cf.PageBackgroundBrush") as System.Windows.Media.Brush;
        NetworkStepText.Foreground = secondStep
            ? FindResource("Cf.TextPrimaryBrush") as System.Windows.Media.Brush
            : FindResource("Cf.TextSecondaryBrush") as System.Windows.Media.Brush;

        ClearError();
    }

    private bool ValidateBasicStep()
    {
        var subscriptionId = SelectedSubscriptionId();
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return ShowError("请选择或输入目标订阅 ID。");
        }

        if (string.IsNullOrWhiteSpace(ResourceGroupBox.Text))
        {
            return ShowError("请输入已有资源组名称。");
        }

        var region = RegionBox.Text.Trim();
        if (!Regex.IsMatch(region, "^[a-z0-9]{2,64}$", RegexOptions.CultureInvariant))
        {
            return ShowError("区域必须是 Azure 区域短名称，例如 koreacentral。");
        }

        var vmName = VmNameBox.Text.Trim();
        if (!VmNamePattern.IsMatch(vmName))
        {
            return ShowError("虚拟机名称只能包含字母、数字和连字符，且不能以连字符开头或结尾。");
        }

        if (IsWindowsImage() && vmName.Length > 15)
        {
            return ShowError("Windows 虚拟机名称最多 15 个字符。");
        }

        if (string.IsNullOrWhiteSpace(SelectedTag(ImageBox)) || string.IsNullOrWhiteSpace(SelectedContent(VmSizeBox)))
        {
            return ShowError("请选择镜像和规格。");
        }

        if (string.IsNullOrWhiteSpace(AdminUsernameBox.Text))
        {
            return ShowError("请输入管理员用户名。");
        }

        ClearError();
        return true;
    }

    private bool ValidateNetworkAndCredentials()
    {
        var subnetId = SubnetIdBox.Text.Trim();
        if (!subnetId.Contains("/subnets/", StringComparison.OrdinalIgnoreCase))
        {
            return ShowError("请输入完整的已有子网 Resource ID（…/virtualNetworks/…/subnets/…）。");
        }

        if (PasswordAuthBox.IsChecked is true)
        {
            var minimumLength = IsWindowsImage() ? 8 : 6;
            if (AdminPasswordBox.Password.Length < minimumLength)
            {
                return ShowError($"管理员密码至少需要 {minimumLength} 个字符；Azure 还会校验复杂度。");
            }

            if (string.IsNullOrWhiteSpace(CredentialNameBox.Text))
            {
                return ShowError("请输入要保存到全局凭据库的凭据名称。");
            }
        }
        else if (string.IsNullOrWhiteSpace(SshPublicKeyBox.Text) ||
                 !SshPublicKeyBox.Text.TrimStart().StartsWith("ssh-", StringComparison.OrdinalIgnoreCase))
        {
            return ShowError("请输入以 ssh- 开头的 SSH 公钥；不要粘贴私钥。");
        }

        ClearError();
        return true;
    }

    private bool IsWindowsImage() => SelectedTag(ImageBox)
        .StartsWith("MicrosoftWindowsServer:", StringComparison.OrdinalIgnoreCase);

    private string SelectedSubscriptionId() => SubscriptionBox.SelectedItem is ProvisioningSubscriptionOption item
        ? item.SubscriptionId
        : SubscriptionBox.Text.Trim();

    private static string SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private static string SelectedContent(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;

    private bool ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        return false;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }
}

/// <summary>创建向导的订阅下拉项。显示名与 Resource ID 分开，避免把显示文本误提交到 ARM。</summary>
public sealed class ProvisioningSubscriptionOption(string subscriptionId, string displayName)
{
    public string SubscriptionId { get; } = subscriptionId;

    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName)
        ? subscriptionId
        : $"{displayName} ({subscriptionId})";
}

/// <summary>
/// 创建向导的确认结果。<see cref="Parameters"/> 永远不含密码；<see cref="Password"/> 仅用于随后的
/// DPAPI 凭据库写入，调用方不得记录、序列化或插入 OperationRequest Payload。
/// </summary>
public sealed class CreateVmResult(
    string subscriptionId,
    string resourceGroupName,
    string region,
    IReadOnlyDictionary<string, string> parameters,
    string? password,
    string? passwordCredentialName)
{
    public string SubscriptionId { get; } = subscriptionId;
    public string ResourceGroupName { get; } = resourceGroupName;
    public string Region { get; } = region;
    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters;
    public string? Password { get; } = password;
    public string? PasswordCredentialName { get; } = passwordCredentialName;

    public override string ToString() =>
        $"CreateVmResult({SubscriptionId}, {ResourceGroupName}, {Region}, Password={Password is not null})";
}

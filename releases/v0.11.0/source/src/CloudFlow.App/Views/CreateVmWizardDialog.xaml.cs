using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CloudFlow.App.Themes;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;

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

    private static readonly Regex NetworkResourceNamePattern = new(
        @"^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,79}$", RegexOptions.Compiled);

    private static readonly Regex CidrPattern = new(
        @"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}/(3[0-2]|[12]?\d)$",
        RegexOptions.Compiled);

    private bool _isSecondStep;
    private bool _settingCredentialSuggestion;
    private bool _credentialNameWasEdited;
    private bool _settingVnetSuggestion;
    private bool _vnetNameWasEdited;
    private bool _settingRegionFromResourceGroup;
    private bool _regionWasEdited;
    private readonly IVmPriceCatalog _priceCatalog;
    private readonly IVmImageCatalog _imageCatalog;
    private CancellationTokenSource? _priceCts;
    private CancellationTokenSource? _imageCompatCts;

    /// <summary>
    /// 点"创建"通过基本校验后立即关闭——真正的提交（可能要几十秒到几分钟）在关闭之后于
    /// 主窗口后台跑，进度靠顶栏"任务进行中"徽标和列表页横幅看，不再堵在这个对话框里
    /// （对齐 Azure Portal 自己"点创建 → 关闭创建面板 → 弹通知 → 到通知中心看进度"这套习惯）。
    /// </summary>
    public CreateVmWizardDialog(
        IReadOnlyList<ProvisioningSubscriptionOption> subscriptions,
        IReadOnlyList<ResourceGroupOption> resourceGroups,
        IReadOnlyList<RegionOption> regions,
        IReadOnlyList<VmSizeOption> vmSizes,
        IVmPriceCatalog priceCatalog,
        IVmImageCatalog imageCatalog)
    {
        InitializeComponent();
        _priceCatalog = priceCatalog;
        _imageCatalog = imageCatalog;

        SubscriptionBox.ItemsSource = subscriptions;
        SubscriptionBox.DisplayMemberPath = nameof(ProvisioningSubscriptionOption.DisplayName);
        SubscriptionBox.SelectedValuePath = nameof(ProvisioningSubscriptionOption.SubscriptionId);
        ResourceGroupBox.ItemsSource = resourceGroups;
        RegionBox.ItemsSource = regions;
        VmSizeBox.ItemsSource = vmSizes;

        if (subscriptions.Count == 1)
        {
            SubscriptionBox.SelectedIndex = 0;
        }

        // 默认选中一个已用过的资源组，省得每次都要自己挑——对应区域紧接着由
        // ResourceGroupBox_SelectionChanged 自动带出来，不需要在这里重复选区域。
        if (resourceGroups.Count > 0)
        {
            ResourceGroupBox.SelectedIndex = 0;
        }
        else if (regions.Count > 0)
        {
            RegionBox.SelectedIndex = 0;
        }

        ImageBox.SelectedIndex = 0;
        if (vmSizes.Count > 0)
        {
            VmSizeBox.SelectedIndex = 0;
        }

        UpdateCredentialPanels();

        ResourceGroupBox.SelectionChanged += ResourceGroupBox_SelectionChanged;
        // ComboBox 本身没有公开的 TextChanged 事件（可编辑状态下改文字的是内部模板里那个
        // TextBox），要拿到"用户正在打字"必须挂内部路由事件——账户下资源组一多，原来的
        // 下拉是完全不筛选的原始清单，只能自己一条条翻，这里补上"边打字边筛选"。
        ResourceGroupBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ResourceGroupBox_TextChanged));
        RegionBox.SelectionChanged += RegionBox_Changed;
        RegionBox.LostKeyboardFocus += RegionBox_Changed;
        VmSizeBox.SelectionChanged += (_, _) => { RefreshPriceEstimate(); RefreshImageCompatibility(); };
        VmSizeBox.LostKeyboardFocus += (_, _) => { RefreshPriceEstimate(); RefreshImageCompatibility(); };
        ImageBox.LostKeyboardFocus += (_, _) => { RefreshPriceEstimate(); RefreshImageCompatibility(); };
        RefreshPriceEstimate();
        RefreshImageCompatibility();
    }

    /// <summary>选了一个已有资源组就把区域自动带过去——资源组的区域建好就不可变，
    /// 选错区域会撞上 409 InvalidResourceGroupLocation（真实踩过的坑）。
    /// 用户已经自己动过区域框（<see cref="_regionWasEdited"/>）之后就不再覆盖，
    /// 尊重那是一次有意的选择（比如明知资源组在别的区域，就是要用这个资源组）。</summary>
    private void ResourceGroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _regionWasEdited)
        {
            return;
        }

        if (ResourceGroupBox.SelectedItem is not ResourceGroupOption option ||
            string.IsNullOrEmpty(option.Location))
        {
            return;
        }

        _settingRegionFromResourceGroup = true;
        if (RegionBox.ItemsSource is IEnumerable<RegionOption> regions &&
            regions.FirstOrDefault(r =>
                string.Equals(r.Name, option.Location, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            RegionBox.SelectedItem = match;
        }
        else
        {
            RegionBox.Text = option.Location;
        }

        _settingRegionFromResourceGroup = false;
        RefreshPriceEstimate();
        RefreshImageCompatibility();
    }

    /// <summary>
    /// 边打字边筛选下拉里的已有资源组（按名称包含匹配），不影响 <c>Text</c> 本身——
    /// 打算新建一个全新名字时照样能继续打完、提交，不会被这里的筛选拦住或改写。
    /// 只是筛哪些项在下拉里显示，跟 <see cref="SelectedItemIfTextUnchanged{T}"/> 判断
    /// 用户是否仍在使用某个已有项这件事完全独立、互不影响。
    /// </summary>
    private void ResourceGroupBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyResourceGroupFilter();

        // 有匹配就把下拉打开，筛选结果才看得见——IsTextSearchEnabled=False 之后 WPF
        // 不会自动展开，得自己控制。
        if (!string.IsNullOrEmpty(ResourceGroupBox.Text))
        {
            ResourceGroupBox.IsDropDownOpen = true;
        }
    }

    private void ApplyResourceGroupFilter()
    {
        if (ResourceGroupBox.ItemsSource is not { } source)
        {
            return;
        }

        var keyword = ResourceGroupBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(source);
        view.Filter = keyword.Length == 0
            ? null
            : item => item is ResourceGroupOption option &&
                      option.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void RegionBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingRegionFromResourceGroup)
        {
            _regionWasEdited = true;
        }

        RefreshPriceEstimate();
        RefreshImageCompatibility();
    }

    /// <summary>
    /// 光标标"规格仅支持 Gen1"不够用——用户还得知道自己选的镜像到底是 Gen1 还是 Gen2，
    /// 两边对不上才是真正会被拒绝的组合。查到镜像世代后，跟当前选中规格的
    /// <see cref="VmSizeOption.HyperVGenerations"/> 交叉核对，不匹配时用醒目颜色提前示警，
    /// 不等提交失败才知道——但这只是提示，不拦提交：查询失败/结果不确定时不显示，不影响创建流程。
    /// </summary>
    private async void RefreshImageCompatibility()
    {
        if (!IsLoaded)
        {
            return;
        }

        var subscriptionId = SelectedSubscriptionId();
        var region = SelectedRegionName();
        var imageUrn = SelectedTag(ImageBox);
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(region) ||
            string.IsNullOrWhiteSpace(imageUrn))
        {
            ImageCompatibilityText.Text = string.Empty;
            return;
        }

        _imageCompatCts?.Cancel();
        var cts = new CancellationTokenSource();
        _imageCompatCts = cts;

        try
        {
            var generation = await _imageCatalog
                .GetHyperVGenerationAsync(subscriptionId, region, imageUrn, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (string.IsNullOrEmpty(generation))
            {
                ImageCompatibilityText.Text = string.Empty;
                return;
            }

            var sizeGenerations = (VmSizeBox.SelectedItem as VmSizeOption)?.HyperVGenerations;
            var compatible = sizeGenerations is null ||
                              sizeGenerations.Contains(generation, StringComparison.OrdinalIgnoreCase);
            var friendlyGen = FriendlyGeneration(generation);

            ImageCompatibilityText.Text = compatible
                ? $"该镜像为 {friendlyGen}。"
                : $"⚠ 该镜像为 {friendlyGen}，但当前规格不支持 {friendlyGen}，这个组合会被 Azure 拒绝，请换一个规格或镜像。";
            ImageCompatibilityText.Foreground = (compatible
                ? FindResource("Cf.TextSecondaryBrush")
                : FindResource("Cf.Status.Danger.Fg")) as System.Windows.Media.Brush;
        }
        catch (OperationCanceledException)
        {
            // 用户已经切换到另一个区域/镜像/规格，这次查询的结果不再需要。
        }
    }

    private static string FriendlyGeneration(string raw) => raw.ToUpperInvariant() switch
    {
        "V1" => "Hyper-V Gen1",
        "V2" => "Hyper-V Gen2",
        _ => raw
    };

    /// <summary>
    /// 打开对话框时区域/规格目录还没查回来（先用推断/演示清单垫上，避免阻塞对话框弹出），
    /// 真实数据到了之后由 ViewModel 回调这两个方法替换下拉内容。
    /// 只换 ItemsSource，不动 Text——用户这几百毫秒里已经选的/正在打的东西不会被冲掉；
    /// 只有当前还停在"默认第一项"时才顺手把选中项也换成新清单的第一项。
    /// </summary>
    public void UpdateRegionOptions(IReadOnlyList<RegionOption> regions)
    {
        if (regions.Count == 0)
        {
            return;
        }

        var wasOnDefault = RegionBox.SelectedIndex == 0;
        RegionBox.ItemsSource = regions;
        if (wasOnDefault)
        {
            RegionBox.SelectedIndex = 0;
        }
    }

    public void UpdateVmSizeOptions(IReadOnlyList<VmSizeOption> vmSizes)
    {
        if (vmSizes.Count == 0)
        {
            return;
        }

        var wasOnDefault = VmSizeBox.SelectedIndex == 0;
        VmSizeBox.ItemsSource = vmSizes;
        if (wasOnDefault)
        {
            VmSizeBox.SelectedIndex = 0;
        }
    }

    /// <summary>真实资源组列表回来后替换掉推断清单；如果用户还停在默认第一项（没有自己选/打过
    /// 别的），顺手把选中项也换成新清单的第一项——这样默认预填的就是真实存在的资源组，
    /// 而不是"账户下已知虚拟机反推"出来的旧清单里那个。已经自己选/打过东西就不再覆盖。</summary>
    public void UpdateResourceGroupOptions(IReadOnlyList<ResourceGroupOption> groups)
    {
        if (groups.Count == 0)
        {
            return;
        }

        var wasOnDefault = ResourceGroupBox.SelectedIndex == 0;
        ResourceGroupBox.ItemsSource = groups;
        if (wasOnDefault)
        {
            ResourceGroupBox.SelectedIndex = 0;
        }
        else
        {
            // 换了一份新的 ItemsSource 就是换了一个新的 CollectionView 实例，之前打字筛选出来的
            // Filter 不会跟着带过去——用户如果已经在筛选框里打了字（真实列表还没回来之前），
            // 换上真实列表后筛选条件要继续生效，不能变回"看到全部未筛选的清单"。
            ApplyResourceGroupFilter();
        }
    }

    /// <summary>当前选中/输入的区域短名称，供 ViewModel 按"用户实际选的区域"续查规格目录用。</summary>
    public string CurrentRegionName => SelectedRegionName();

    /// <summary>
    /// 预估月费：区域/规格/镜像（操作系统）任一变化都会重新查一次 Azure 公开零售价目表。
    /// 查询失败或没有命中价目时清空文字，不阻塞创建流程——这只是参考信息。
    /// </summary>
    private async void RefreshPriceEstimate()
    {
        if (!IsLoaded)
        {
            return;
        }

        var region = SelectedRegionName();
        var size = SelectedVmSizeName();
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(size))
        {
            PriceEstimateText.Text = string.Empty;
            return;
        }

        _priceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _priceCts = cts;
        var isWindows = IsWindowsImage();

        try
        {
            var estimate = await _priceCatalog
                .GetMonthlyEstimateUsdAsync(size, region, isWindows, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            PriceEstimateText.Text = estimate is { } usd
                ? $"预估约 ${usd:0.##}/月（仅计算费用，不含存储/网络/许可证，实际以 Azure 账单为准）"
                : "该区域/规格组合暂无可用的预估价格。";
        }
        catch (OperationCanceledException)
        {
            // 用户已经切换到另一个区域/规格，这次查询的结果不再需要。
        }
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
            [CreateVmHandler.PayloadVirtualNetwork] = VirtualNetworkBox.Text.Trim(),
            [CreateVmHandler.PayloadVnetAddressSpace] = VnetAddressSpaceBox.Text.Trim(),
            [CreateVmHandler.PayloadSubnetName] = SubnetNameBox.Text.Trim(),
            [CreateVmHandler.PayloadSubnetAddressPrefix] = SubnetAddressPrefixBox.Text.Trim(),
            [CreateVmHandler.PayloadVmSize] = SelectedVmSizeName(),
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
            SelectedResourceGroupName(),
            SelectedRegionName(),
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
        if (!IsLoaded)
        {
            return;
        }

        RefreshPriceEstimate();
        RefreshImageCompatibility();

        if (!IsWindowsImage())
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

        if (!_vnetNameWasEdited)
        {
            SetSuggestedVirtualNetworkName();
        }
    }

    private void CredentialNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingCredentialSuggestion)
        {
            _credentialNameWasEdited = true;
        }
    }

    private void VirtualNetworkBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingVnetSuggestion)
        {
            _vnetNameWasEdited = true;
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

    /// <summary>虚拟网络名称跟着虚拟机名走，和 Azure Portal 创建向导的默认命名习惯（"{名称}-vnet"）
    /// 保持一致——用户没有理由去记一个新名字，抄门户的默认值最省心。</summary>
    private void SetSuggestedVirtualNetworkName()
    {
        _settingVnetSuggestion = true;
        VirtualNetworkBox.Text = string.IsNullOrWhiteSpace(VmNameBox.Text)
            ? string.Empty
            : $"{VmNameBox.Text.Trim()}-vnet";
        _settingVnetSuggestion = false;
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
            return ShowError("请输入资源组名称（不存在会自动创建）。");
        }

        var region = SelectedRegionName();
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

        if (string.IsNullOrWhiteSpace(SelectedTag(ImageBox)) || string.IsNullOrWhiteSpace(SelectedVmSizeName()))
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
        var vnet = VirtualNetworkBox.Text.Trim();
        if (!vnet.Contains("/virtualNetworks/", StringComparison.OrdinalIgnoreCase) &&
            !NetworkResourceNamePattern.IsMatch(vnet))
        {
            return ShowError("虚拟网络请填写名称，或粘贴完整 Resource ID（…/virtualNetworks/…）。");
        }

        var subnetName = SubnetNameBox.Text.Trim();
        if (!NetworkResourceNamePattern.IsMatch(subnetName))
        {
            return ShowError("子网名称不合法。");
        }

        if (!CidrPattern.IsMatch(VnetAddressSpaceBox.Text.Trim()))
        {
            return ShowError("虚拟网络地址空间不是合法的 CIDR，例如 10.0.0.0/16（仅虚拟网络不存在时用于新建）。");
        }

        if (!CidrPattern.IsMatch(SubnetAddressPrefixBox.Text.Trim()))
        {
            return ShowError("子网地址段不是合法的 CIDR，例如 10.0.0.0/24（仅子网不存在时用于新建）。");
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
        .StartsWith("MicrosoftWindows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 可编辑 ComboBox 的通用兜底：WPF 的 <c>IsEditable</c> ComboBox 在用户手打文本盖掉原来选中项
    /// 之后，<c>SelectedItem</c> 并不会跟着清空——它会一直指向那个已经不再显示在框里的旧选项，直到
    /// 用户重新从下拉里选一次。真实踩过的坑：资源组框预选了一个已有资源组（默认带出上次用过的一个），
    /// 用户打算新建另一个资源组、把文本整个替换掉，<c>SelectedItem</c> 却仍停在原来那个已有资源组
    /// 上；如果只看 <c>SelectedItem</c> 不看 <c>Text</c> 是否还对得上，创建时就会用错资源组。
    /// 因此只有当 <c>Text</c> 仍与 <c>displayText(item)</c> 完全一致（说明用户没有动过）时才信任
    /// <c>SelectedItem</c>，否则一律以 <c>Text</c> 为准——这条规则对本对话框里每一个可编辑下拉框
    /// （订阅/资源组/区域/镜像/规格）都成立，统一在这里处理，不在每个 SelectedXxx 方法里各写一份。
    /// </summary>
    private static T? SelectedItemIfTextUnchanged<T>(ComboBox box, Func<T, string> displayText)
        where T : class =>
        box.SelectedItem is T item && string.Equals(box.Text, displayText(item), StringComparison.Ordinal)
            ? item
            : null;

    private string SelectedSubscriptionId() =>
        SelectedItemIfTextUnchanged<ProvisioningSubscriptionOption>(SubscriptionBox, o => o.DisplayName) is { } item
            ? item.SubscriptionId
            : SubscriptionBox.Text.Trim();

    // ImageBox 的选项是 ComboBoxItem（Tag=URN，Content=展示名）；IsEditable=True 允许直接输入
    // 自定义 URN，此时 Text 不再等于任何选项的展示名，退回读 Text（就是用户输入的 URN 本身）。
    private static string SelectedTag(ComboBox box) =>
        SelectedItemIfTextUnchanged<ComboBoxItem>(box, i => i.Content?.ToString() ?? "") is { } item
            ? item.Tag as string ?? string.Empty
            : box.Text.Trim();

    // ResourceGroupBox 的选项是 ResourceGroupOption（ToString()=名称+区域），选中已有项时不能
    // 直接读 Text——那会把"myrg · koreacentral"这种展示文本当成资源组名字提交上去。
    private string SelectedResourceGroupName() =>
        SelectedItemIfTextUnchanged<ResourceGroupOption>(ResourceGroupBox, o => o.ToString()) is { } option
            ? option.Name
            : ResourceGroupBox.Text.Trim();

    // RegionBox 的选项是 RegionOption（Name=ARM 短名称，ToString()=展示用英文/中文名+短名称）。
    private string SelectedRegionName() =>
        SelectedItemIfTextUnchanged<RegionOption>(RegionBox, o => o.ToString()) is { } option
            ? option.Name
            : RegionBox.Text.Trim();

    // VmSizeBox 的选项是 VmSizeOption（Name=规格名，ToString()=规格名+vCPU/内存），同上原则。
    private string SelectedVmSizeName() =>
        SelectedItemIfTextUnchanged<VmSizeOption>(VmSizeBox, o => o.ToString()) is { } option
            ? option.Name
            : VmSizeBox.Text.Trim();

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
/// 创建向导的资源组下拉项。选中一个已有资源组时，<see cref="Location"/> 用来把区域框自动
/// 带过去——资源组一旦建好区域不可变，选错区域会撞上 409 InvalidResourceGroupLocation（真实
/// 踩过的坑）。<see cref="ToString"/> 把区域也带出来，方便同名资源组分布在多个区域时分辨。
/// </summary>
public sealed record ResourceGroupOption(string Name, string Location)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Location) ? Name : $"{Name} · {Location}";
}

/// <summary>
/// 创建向导的区域下拉项。<see cref="Name"/> 是提交给 ARM 的短名称（如 koreacentral），
/// <see cref="ToString"/> 把它和英文/中文展示名拼在一起——只看 "koreacentral" 这种原始参数
/// 认不出是哪个区域，拼上展示名才能一眼确认选的是不是自己要的地方。
/// </summary>
public sealed record RegionOption(string Name, string DisplayName)
{
    public override string ToString()
    {
        var chinese = ChineseNames.GetValueOrDefault(Name);
        return chinese is null ? $"{DisplayName} · {Name}" : $"{DisplayName}（{chinese}） · {Name}";
    }

    /// <summary>常见 Azure 公有云区域的中文名，覆盖不到的区域只显示英文展示名，不影响功能。</summary>
    private static readonly Dictionary<string, string> ChineseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eastus"] = "美国东部", ["eastus2"] = "美国东部2", ["southcentralus"] = "美国中南部",
        ["westus"] = "美国西部", ["westus2"] = "美国西部2", ["westus3"] = "美国西部3",
        ["centralus"] = "美国中部", ["northcentralus"] = "美国中北部", ["westcentralus"] = "美国中西部",
        ["canadacentral"] = "加拿大中部", ["canadaeast"] = "加拿大东部",
        ["brazilsouth"] = "巴西南部", ["brazilsoutheast"] = "巴西东南部",
        ["northeurope"] = "欧洲北部", ["westeurope"] = "欧洲西部",
        ["uksouth"] = "英国南部", ["ukwest"] = "英国西部",
        ["francecentral"] = "法国中部", ["francesouth"] = "法国南部",
        ["germanywestcentral"] = "德国中西部", ["germanynorth"] = "德国北部",
        ["norwayeast"] = "挪威东部", ["norwaywest"] = "挪威西部",
        ["switzerlandnorth"] = "瑞士北部", ["switzerlandwest"] = "瑞士西部",
        ["swedencentral"] = "瑞典中部", ["polandcentral"] = "波兰中部",
        ["italynorth"] = "意大利北部", ["spaincentral"] = "西班牙中部",
        ["uaenorth"] = "阿联酋北部", ["uaecentral"] = "阿联酋中部",
        ["southafricanorth"] = "南非北部", ["southafricawest"] = "南非西部",
        ["australiaeast"] = "澳大利亚东部", ["australiasoutheast"] = "澳大利亚东南部",
        ["australiacentral"] = "澳大利亚中部", ["australiacentral2"] = "澳大利亚中部2",
        ["centralindia"] = "印度中部", ["southindia"] = "印度南部", ["westindia"] = "印度西部",
        ["jioindiawest"] = "Jio 印度西部", ["jioindiacentral"] = "Jio 印度中部",
        ["eastasia"] = "东亚", ["southeastasia"] = "东南亚",
        ["japaneast"] = "日本东部", ["japanwest"] = "日本西部",
        ["koreacentral"] = "韩国中部", ["koreasouth"] = "韩国南部",
        ["qatarcentral"] = "卡塔尔中部", ["israelcentral"] = "以色列中部",
        ["mexicocentral"] = "墨西哥中部", ["newzealandnorth"] = "新西兰北部",
        ["indonesiacentral"] = "印度尼西亚中部", ["malaysiawest"] = "马来西亚西部",
        ["chilecentral"] = "智利中部"
    };
}

/// <summary>
/// 创建向导的规格下拉项。<see cref="VCpus"/>/<see cref="MemoryMb"/> 缺失时（比如目录查询失败）
/// 只显示规格名本身——只看 "Standard_B2s" 认不出配置大小，拼上核数/内存才能一眼确认够不够用。
/// <see cref="HyperVGenerations"/> 不含 "V2" 时额外标注"仅 Gen1"——老规格系列（如 A 系列）
/// 配现在市面上大多数默认 Gen2 的新镜像会被 Azure 直接拒绝（400 "cannot boot Hypervisor
/// Generation"），标出来能让人下拉时就避开，不用等提交失败才知道。
/// </summary>
public sealed record VmSizeOption(
    string Name, int? VCpus = null, int? MemoryMb = null, string? HyperVGenerations = null)
{
    public override string ToString()
    {
        if (VCpus is null || MemoryMb is null)
        {
            return Name;
        }

        var gen1Only = HyperVGenerations is not null &&
                       !HyperVGenerations.Contains("V2", StringComparison.OrdinalIgnoreCase);
        var suffix = gen1Only ? "，仅支持 Gen1 镜像" : "";
        return $"{Name}（{VCpus} vCPU / {MemoryMb.Value / 1024.0:0.#} GB{suffix}）";
    }
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

/// <summary>提交结果：失败时 <see cref="ErrorMessage"/> 是给用户看的简短原因，直接喂给对话框的 ShowError。</summary>
public sealed record CreateVmSubmitOutcome(bool Success, string? ErrorMessage)
{
    public static CreateVmSubmitOutcome Ok() => new(true, null);
    public static CreateVmSubmitOutcome Failed(string message) => new(false, message);
}

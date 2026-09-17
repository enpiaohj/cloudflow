using System.Windows;
using CloudFlow.App.Infrastructure;
using CloudFlow.App.ViewModels;
using CloudFlow.App.Views;
using CloudFlow.Azure.ResourceGraph;
using CloudFlow.Azure.Arm;
using CloudFlow.Azure.Auth;
using CloudFlow.Core.Identity;
using CloudFlow.Core.Operations;
using CloudFlow.Core.Scopes;
using CloudFlow.Data.Stores;
using CloudFlow.Modules.Compute.Operations;
using CloudFlow.Modules.Compute.Services;
using System.Net.Http;
using CloudFlow.Modules.Network.Operations;
using CloudFlow.Modules.Network.Services;
using CloudFlow.Operations.Pipeline;
using CloudFlow.Operations.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudFlow.App;

/// <summary>
/// 应用入口：组装 DI 容器。
/// 当前为 Demo 模式（Mock 服务）；接入真实 Azure 时替换对应注册即可，
/// 架构与 UI 不变。
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 崩溃日志：所有未处理异常落盘（%LOCALAPPDATA%\CloudFlow\logs）
        DispatcherUnhandledException += (_, args) =>
        {
            CloudFlow.Data.Stores.CloudFlowPaths.WriteCrashLog("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"发生未处理的异常：\n\n{args.Exception.Message}\n\n{args.Exception.GetType().Name}\n\n" +
                $"日志：{CloudFlow.Data.Stores.CloudFlowPaths.LogsDirectory}",
                "CloudFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CloudFlow.Data.Stores.CloudFlowPaths.WriteCrashLog("AppDomain.UnhandledException", ex);
            }
        };
        // 未被 await/观测的 Task 异常（常见于 fire-and-forget 的异步命令）只落日志、不弹窗：
        // 触发时机与当前 UI 操作已经脱节，弹窗只会打扰用户；SetObserved 避免终结器线程再抛一次。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CloudFlow.Data.Stores.CloudFlowPaths.WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Services = BuildServices();

        // 先安装那批**可变**画刷实例，再按设置改色。
        // 顺序不能反：App.xaml 不合并 CfPalette.*.xaml（理由见该文件注释），
        // 在 Initialize() 之前 Application.Resources 里根本没有 Cf.* 这些 key，
        // Apply() 会以"画刷不在 Application.Resources 中"直接抛。
        CloudFlow.App.Themes.CfThemeManager.Initialize();

        // 主题必须在窗口出现之前落地，否则用户会先看到一帧浅色再跳成深色。
        // 这也是「设置读写不能挂在设置页上」的原因：SettingsViewModel 是首次导航到设置页才构造的。
        CloudFlow.App.Themes.CfThemeManager.Apply(
            Services.GetRequiredService<AppSettingsStore>().Current.Theme);

        var window = Services.GetRequiredService<MainWindow>();
        window.Show();

        // 跟随系统时才挂钩子；SystemThemeWatcher 要在窗口句柄上装消息钩子，Show 之前句柄还没生成
        CloudFlow.App.Themes.CfThemeManager.WatchSystemTheme(window);
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // ---- 配置 ----
        // 两个来源，后者覆盖前者：
        //  ① 程序目录的 appsettings.json：开发调试用（含本机 ClientId，gitignore，不随发布产物分发）；
        //  ② %LOCALAPPDATA%\CloudFlow\appsettings.json：用户在「设置 → 账户 → 登录服务」里填写后写入。
        //     单文件发布包里没有 ①，② 是正式版唯一的配置入口。
        var userConfigFile = System.IO.Path.Combine(CloudFlowPaths.Root, MsalAuthConfig.UserConfigFileName);
        IConfiguration configuration;
        try
        {
            configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile(userConfigFile, optional: true, reloadOnChange: false)
                .Build();
        }
        catch (Exception ex) when (ex is FormatException or System.IO.InvalidDataException)
        {
            // 用户目录下的配置被手工改坏时不能让程序起不来：记录后退回只读程序目录的配置，
            // 设置页会显示"未配置"，在那里重新保存一次即可覆盖坏文件。
            CloudFlowPaths.WriteCrashLog($"读取用户配置 {userConfigFile}", ex);
            configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();
        }
        services.AddSingleton<IConfiguration>(configuration);
        var authConfig = configuration.GetSection(MsalAuthConfig.SectionName).Get<MsalAuthConfig>()
                         ?? new MsalAuthConfig();
        services.AddSingleton(authConfig);

        // ---- 日志 ----
        services.AddLogging(builder => builder.AddDebug());

        // ---- Platform：Identity / Scope ----
        services.AddSingleton(authConfig);
        services.AddSingleton<ScopeContext>();
        services.AddSingleton<IAccountSessionManager, MsalAccountSessionManager>();
        services.AddSingleton<SubscriptionDiscoveryService>();
        services.AddSingleton<ISubscriptionDiscoveryService>(sp => sp.GetRequiredService<SubscriptionDiscoveryService>());

        // 统一身份 Provider（P0 Spike）：MSAL（企业）+ 嵌入式 Azure CLI（个人）
        services.AddSingleton<CloudFlow.Azure.Identity.AzureCli.AzureCliProcessRunner>();
        services.AddSingleton<CloudFlow.Azure.Identity.AzureCli.IAzureCliProcessRunner>(
            sp => sp.GetRequiredService<CloudFlow.Azure.Identity.AzureCli.AzureCliProcessRunner>());
        services.AddSingleton<CloudFlow.Azure.Identity.AzureCli.AzureCliProfileManager>();
        services.AddSingleton<CloudFlow.Azure.Identity.AzureCli.AzureCliRuntimeManager>();
        services.AddSingleton<ICloudIdentityProvider, CloudFlow.Azure.Identity.Msal.MsalIdentityProvider>();
        services.AddSingleton<ICloudIdentityProvider>(sp => new CloudFlow.Azure.Identity.AzureCli.EmbeddedAzureCliIdentityProvider(
            sp.GetRequiredService<CloudFlow.Azure.Identity.AzureCli.AzureCliProcessRunner>(),
            sp.GetRequiredService<CloudFlow.Azure.Identity.AzureCli.AzureCliProfileManager>(),
            sp.GetRequiredService<CloudFlow.Azure.Identity.AzureCli.AzureCliRuntimeManager>()));
        services.AddSingleton<IAzureClientFactory, CloudArmClientFactory>();

        // 账户目录：合并 MSAL 企业账户与个人 Microsoft 账户，并按 ProviderType 路由
        services.AddSingleton<IPersonalAccountRegistry, PersonalAccountStore>();
        services.AddSingleton<CloudFlow.Azure.Identity.CloudAccountDirectory>();

        // ---- Data ----
        services.AddSingleton<SavedScopeStore>();
        services.AddSingleton<ActiveAccountStore>();
        services.AddSingleton<IAuditLog, AuditFileLog>();

        // 应用设置：构造时即读盘，所以必须在 window.Show() 之前被解析到 ——
        // 主题要在窗口出现前应用，否则会先闪一下浅色再切深色。
        var settingsStore = new AppSettingsStore();
        services.AddSingleton(settingsStore);

        // 审批策略读的是上面的设置，每次判定现读，所以设置页改完立即生效
        services.AddSingleton<CloudFlow.Core.Operations.IApprovalPolicy, SettingsApprovalPolicy>();

        // ---- Operations（Operation Engine + Handlers）----
        // 提交端唯一的身份注入点：把当前账户 / 租户 / Provider 盖进 OperationRequest（设计文档 §31）
        services.AddSingleton<CloudFlow.Core.Operations.OperationRequestFactory>();
        // Job 历史持久化到 %LOCALAPPDATA%\CloudFlow\jobs.json，重启不丢失
        services.AddSingleton<IJobStore, JsonJobStore>();
        services.AddSingleton<IOperationEngine, OperationEngine>();
        // ---------- 写操作执行器（Provider 无关 Handler 的底座）----------
        // 契约在 Modules，ARM 实现在 CloudFlow.Azure，此处按请求携带的 ProviderType 路由
        services.AddSingleton<MockVmPowerExecutor>();
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmPowerExecutor>();
        services.AddSingleton<IVmPowerExecutor, VmPowerExecutorRouter>();

        // NSG 规则写操作：契约在 Modules.Network，ARM 实现在 CloudFlow.Azure，此处按 ProviderType 路由
        services.AddSingleton<MockVmNetworkRuleExecutor>();
        services.AddSingleton<ISubnetVmCounter, CloudFlow.Azure.Network.ArmSubnetVmCounter>();
        services.AddSingleton<CloudFlow.Azure.Network.ArmVmNetworkRuleExecutor>();
        services.AddSingleton<IVmNetworkRuleExecutor, VmNetworkRuleExecutorRouter>();

        // 磁盘快照写操作：读接口 IVmDiskService 不含写方法，创建动作只经这条链路
        services.AddSingleton<MockVmDiskSnapshotExecutor>();
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmDiskSnapshotExecutor>();
        services.AddSingleton<IVmDiskSnapshotExecutor, VmDiskSnapshotExecutorRouter>();

        // 删除虚拟机（设计文档 v3.1 §87）：契约同样在 Modules，ARM 实现在 CloudFlow.Azure
        services.AddSingleton<MockVmDeleteExecutor>();
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmDeleteExecutor>();
        services.AddSingleton<IVmDeleteExecutor, VmDeleteExecutorRouter>();

        // 删除资源组（"资源"页——清理创建虚拟机流程按需新建、但删除虚拟机时刻意不连带删除
        // 的网络类残留：虚拟网络/子网/资源组本身）：同一套 Handler+Executor+Router 模式
        services.AddSingleton<CloudFlow.Modules.Network.Services.MockResourceGroupDeleteExecutor>();
        services.AddSingleton<CloudFlow.Azure.Resources.ArmResourceGroupDeleteExecutor>();
        services.AddSingleton<
            CloudFlow.Modules.Network.Services.IResourceGroupDeleteExecutor, ResourceGroupDeleteExecutorRouter>();
        services.AddSingleton<
            CloudFlow.Modules.Network.Services.IResourceGroupService,
            CloudFlow.Modules.Network.Services.ResourceGroupService>();

        // 删除单个资源（"所有资源"页——比删资源组更细的粒度，只删这一件，不连带整个资源组）
        services.AddSingleton<CloudFlow.Modules.Network.Services.MockResourceDeleteExecutor>();
        services.AddSingleton<CloudFlow.Azure.Resources.ArmResourceDeleteExecutor>();
        services.AddSingleton<
            CloudFlow.Modules.Network.Services.IResourceDeleteExecutor, ResourceDeleteExecutorRouter>();
        services.AddSingleton<
            CloudFlow.Modules.Network.Services.IResourceService,
            CloudFlow.Modules.Network.Services.ResourceService>();

        // 创建虚拟机（§87）：Router 额外吃 SshCredentialService ——
        // 密码方式创建时载荷只带凭据 Id，明文由本层解出（不落盘），Modules/Azure 层不认识凭据库
        services.AddSingleton<MockVmProvisioningExecutor>();
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmProvisioningExecutor>();
        services.AddSingleton<IVmProvisioningExecutor, VmProvisioningExecutorRouter>();
        services.AddSingleton<IVmProvisioningService, VmProvisioningService>();

        services.AddTransient<IOperationHandler, StartVmHandler>();
        services.AddTransient<IOperationHandler, RestartVmHandler>();
        services.AddTransient<IOperationHandler, PowerOffVmHandler>();
        services.AddTransient<IOperationHandler, DeallocateVmHandler>();
        services.AddTransient<IOperationHandler, ResizeVmHandler>();
        services.AddTransient<IOperationHandler, SnapshotVmHandler>();
        services.AddTransient<IOperationHandler, ChangePortHandler>();
        services.AddTransient<IOperationHandler, OpenPortHandler>();
        services.AddTransient<IOperationHandler, DeleteRuleHandler>();
        services.AddTransient<IOperationHandler, DeleteVmHandler>();
        services.AddTransient<IOperationHandler, CreateVmHandler>();
        services.AddTransient<IOperationHandler, CloudFlow.Modules.Network.Operations.DeleteResourceGroupHandler>();
        services.AddTransient<IOperationHandler, CloudFlow.Modules.Network.Operations.DeleteResourceHandler>();

        // ---- Modules：Compute（Demo / Mock）----
        services.AddSingleton<MockAccountContext>();
        services.AddSingleton<MockVmInventoryService>();
        services.AddSingleton<IVmPowerService, VmPowerService>();
        services.AddSingleton<MockVmDiskService>();
        services.AddSingleton<CloudFlow.Modules.Compute.Models.ComputeModule>();

        // ---- 真实 Azure 数据（登录后自动启用，未登录回退 Mock）----
        services.AddSingleton<ISubscriptionDiscoveryService, SubscriptionDiscoveryService>();
        services.AddSingleton<CloudFlow.Azure.Arm.ArmAccessTokenProvider>();
        services.AddSingleton<ResourceGraphVmInventoryService>();
        services.AddSingleton<IVmInventoryService, HybridVmInventoryService>();

        // 真实磁盘读取（ARM storageProfile + ARG 快照计数）：登录后启用，未登录回退 Mock
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmDiskService>();
        services.AddSingleton<IVmDiskService, HybridVmDiskService>();

        // 虚拟机详情（ARM GET virtualMachines/{name} + 规格目录 + DevTestLab 关闭计划）：
        // 概览页的规格能力 / 安全性 / 休眠 / 创建时间都不在 Resource Graph 的 VM 视图里
        services.AddSingleton<MockVmDetailService>();
        services.AddSingleton<CloudFlow.Azure.Compute.ArmVmDetailService>();
        services.AddSingleton<IVmDetailService, HybridVmDetailService>();

        // 虚拟机规格目录（规格名 → 内存）：ARG 不返回内存，只能由规格推导；内部按 订阅+区域 缓存
        services.AddSingleton<IVmSizeCatalog, CloudFlow.Azure.Compute.ArmVmSizeCatalog>();

        // 区域目录（创建虚拟机向导用）：内部按订阅缓存，未登录/查询失败时返回空列表，
        // 由调用方回退到 Demo 清单——与 IVmSizeCatalog 同一条纪律，只读，不做 Hybrid 包装。
        services.AddSingleton<IRegionCatalog, CloudFlow.Azure.Compute.ArmRegionCatalog>();

        // 资源组目录（创建虚拟机向导用）：列出订阅下全部资源组（不止有 VM 的那些），翻页取全；
        // 同上一条纪律，未登录/查询失败时退回按已知虚拟机反推。
        services.AddSingleton<IResourceGroupCatalog, CloudFlow.Azure.Compute.ArmResourceGroupCatalog>();

        // 镜像目录（创建虚拟机向导用）：查镜像的 Hyper-V 世代，用来提醒"这个镜像和规格搭不上"。
        services.AddSingleton<IVmImageCatalog, CloudFlow.Azure.Compute.ArmVmImageCatalog>();

        // 创建虚拟机向导的预估月费：走 Azure 公开零售价目表，不需要登录，Demo 模式下也能查。
        services.AddSingleton<IVmPriceCatalog, CloudFlow.Azure.Compute.AzureRetailVmPriceCatalog>();

        // 成本洞察：仅登录后读取真实 Cost Management；未登录明确显示“无数据”，绝不伪造账单金额。
        services.AddSingleton<CloudFlow.Azure.Cost.ArmCostService>();
        services.AddSingleton<CloudFlow.Azure.Cost.ICostService, HybridCostService>();

        // Azure Monitor 主机指标（Demo 模式给演示读数；真实账户下不伪造，无数据即显示 "—"）
        services.AddSingleton<MockVmMetricsService>();
        services.AddSingleton<CloudFlow.Azure.Monitoring.ArmVmMetricsService>();
        services.AddSingleton<IVmMetricsService, HybridVmMetricsService>();

        // ---- Modules：Network（Demo / Mock）----
        services.AddSingleton<MockCurrentIpProvider>();
        // 「我的当前 IP」的唯一外发请求出口：向 api.ipify.org / icanhazip.com 查本机出口 IP。
        // Timeout 设为无限，由 HttpPublicIpLookup 自己按每次尝试 5 秒控时 ——
        // 两处都设的话，HttpClient 那条会以不同异常类型先抛，掩盖真正的超时语义。
        services.AddSingleton<IPublicIpLookup>(_ => new HttpPublicIpLookup(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        // 未登录用演示地址；登录后查本机出口 IP，查不到时返回 null，由对话框要求手填
        services.AddSingleton<ICurrentIpProvider, HybridCurrentIpProvider>();
        services.AddSingleton<MockVmNetworkService>();
        // 真实 Azure 网络读取（ARM）：登录后启用，未登录回退 Mock
        // 出站解析按 VM → NIC → IP 配置 → 子网 → NAT GW / 路由表 / LB / 公网 IP 的完整资源图走，
        // 图读取器与 ARG 反查都是无状态单例
        services.AddSingleton<CloudFlow.Azure.Network.ArmOutboundGraphQueries>();
        services.AddSingleton<CloudFlow.Azure.Network.ArmOutboundGraphReader>();
        services.AddSingleton<CloudFlow.Azure.Network.ArmVmNetworkService>();
        services.AddSingleton<IVmNetworkService, HybridVmNetworkService>();

        // ---- ViewModels / Views ----
        // IShellNavigation 与 ShellViewModel 共享同一单例（页面导航统一走 Shell）
        services.AddSingleton<SshConnectionService>();
        // 连接流程：从"用户选了哪条凭据"到"会话建起来"。详情页与 VM 列表共用这一份实现 ——
        // 各写一遍的话，临时输入落库、凭据解析、最近使用记录这三段必然分叉。
        services.AddSingleton<SshConnectFlow>();
        // 批量连接需要的终端面板适配器：把面板抽象成 CloudFlow.Terminal 里的 ISshSessionHost，
        // 让批量编排（去重/剔项/探测/串行/汇总）能在单测里被验证
        services.AddSingleton<TerminalPanelSessionHost>();
        // 终端会话面板：SSH 会话的唯一所有者。必须单例 —— 会话要跨页面存活，
        // 挂在 Transient 的 VmDetailViewModel 上时旧实例的清理永远够不到上一条会话。
        services.AddSingleton<TerminalPanelViewModel>();
        services.AddSingleton<RunningJobsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellViewModel>());
        // 个人账户设备码登录：由 Shell 持有，供顶栏账户菜单与设置页共用
        services.AddSingleton<PersonalSignInViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<VirtualMachinesViewModel>();
        services.AddSingleton<JobsViewModel>();
        services.AddSingleton<ResourceGroupsViewModel>();
        services.AddSingleton<AllResourcesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<VmDetailViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}

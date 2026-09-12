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

        Services = BuildServices();

        var window = Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // ---- 配置（appsettings.json，含 MSAL ClientId，不入库）----
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
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
        services.AddSingleton<IAzureClientFactory, MsalAzureClientFactory>();

        // ---- Data ----
        services.AddSingleton<SavedScopeStore>();
        services.AddSingleton<IAuditLog, AuditFileLog>();

        // ---- Operations（Operation Engine + Handlers）----
        // Job 历史持久化到 %LOCALAPPDATA%\CloudFlow\jobs.json，重启不丢失
        services.AddSingleton<IJobStore, JsonJobStore>();
        services.AddSingleton<IOperationEngine, OperationEngine>();
        services.AddTransient<IOperationHandler, MockStartVmHandler>();
        services.AddTransient<IOperationHandler, MockRestartVmHandler>();
        services.AddTransient<IOperationHandler, MockPowerOffVmHandler>();
        services.AddTransient<IOperationHandler, MockDeallocateVmHandler>();
        services.AddTransient<IOperationHandler, MockResizeVmHandler>();
        services.AddTransient<IOperationHandler, MockSnapshotVmHandler>();
        services.AddTransient<IOperationHandler, ChangePortHandler>();
        services.AddTransient<IOperationHandler, OpenPortHandler>();
        services.AddTransient<IOperationHandler, DeleteRuleHandler>();

        // ---- Modules：Compute（Demo / Mock）----
        services.AddSingleton<MockAccountContext>();
        services.AddSingleton<MockVmInventoryService>();
        services.AddSingleton<IVmPowerService, MockVmPowerService>();
        services.AddSingleton<IVmDiskService, MockVmDiskService>();
        services.AddSingleton<CloudFlow.Modules.Compute.Models.ComputeModule>();

        // ---- 真实 Azure 数据（登录后自动启用，未登录回退 Mock）----
        services.AddSingleton<ISubscriptionDiscoveryService, SubscriptionDiscoveryService>();
        services.AddSingleton<ResourceGraphVmInventoryService>();
        services.AddSingleton<IVmInventoryService, HybridVmInventoryService>();

        // ---- Modules：Network（Demo / Mock）----
        services.AddSingleton<MockCurrentIpProvider>();
        services.AddSingleton<ICurrentIpProvider>(sp => sp.GetRequiredService<MockCurrentIpProvider>());
        services.AddSingleton<MockVmNetworkService>();
        services.AddSingleton<IVmNetworkService>(sp => sp.GetRequiredService<MockVmNetworkService>());

        // ---- ViewModels / Views ----
        // IShellNavigation 与 ShellViewModel 共享同一单例（页面导航统一走 Shell）
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<VirtualMachinesViewModel>();
        services.AddSingleton<JobsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<VmDetailViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}

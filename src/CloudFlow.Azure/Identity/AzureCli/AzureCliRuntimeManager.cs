using System.Text.Json;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// CloudFlow 托管 Azure CLI Runtime 管理（P0 Spike 步骤 6，规范 §十六）。
/// 负责解析 az.cmd 完整路径（禁止 PATH 依赖）、验证存在性与查询认证版本。
/// Runtime 缺失时抛出"通过 CloudFlow 修复"类错误，绝不要求用户安装 Azure CLI。
/// </summary>
public sealed class AzureCliRuntimeManager
{
    private readonly IAzureCliProcessRunner _runner;
    private readonly string? _explicitAzCmdPath;
    private readonly IReadOnlyList<string>? _probePaths;

    public AzureCliRuntimeManager(
        IAzureCliProcessRunner runner,
        string? explicitAzCmdPath = null,
        IReadOnlyList<string>? probePaths = null)
    {
        _runner = runner;
        _explicitAzCmdPath = explicitAzCmdPath;
        _probePaths = probePaths;
    }

    /// <summary>
    /// 解析 az.cmd 完整路径：优先显式指定（安装器写入的配置），
    /// 否则依次探测注入路径（测试/部署自定义）或默认位置：
    /// <see cref="AzureCliRuntimeInstaller"/> 按需下载的落盘目录、安装目录
    /// Runtime\AzureCLI\bin\az.cmd、开发仓库 runtime\AzureCLI\bin\az.cmd。
    /// </summary>
    public string ResolveAzCmd()
    {
        var candidates = new List<string?> { _explicitAzCmdPath };

        if (_probePaths is { } probePaths)
        {
            candidates.AddRange(probePaths);
        }
        else
        {
            // 首次使用时由 AzureCliRuntimeInstaller 按需下载到这里（%LOCALAPPDATA%\CloudFlow\Runtime\
            // AzureCLI），是发布出去的单文件 EXE 唯一真正会命中的候选——发布包本身不携带 Runtime。
            candidates.Add(new AzureCliRuntimeInstaller().AzCmdPath);

            candidates.Add(Path.Combine(AppContext.BaseDirectory, "Runtime", "AzureCLI", "bin", "az.cmd"));

            // 开发环境：解包在仓库根 runtime\ 下（gitignore，不入库）
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                candidates.Add(Path.Combine(directory.FullName, "runtime", "AzureCLI", "bin", "az.cmd"));
                directory = directory.Parent;
            }
        }

        var resolved = candidates
            .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
            .FirstOrDefault();

        return resolved ?? throw new AzureCliException(
            "Azure CLI Runtime 缺失。请通过「添加个人 Microsoft 账户」触发一次自动下载"
            + "（约 90 MB，仅需一次），或检查网络连接后重试。");
    }

    /// <summary>查询认证 Runtime 版本（如 "2.90.0"），用于启动自检与诊断记录。</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(new AzureCliInvocation
        {
            ExecutablePath = ResolveAzCmd(),
            Arguments = ["version", "--output", "json"]
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new AzureCliException(
                $"Azure CLI Runtime 验证失败（退出码 {result.ExitCode}）：{AzureCliOutputRedactor.Redact(result.StandardError)}",
                result.ExitCode);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("azure-cli").GetString()
            ?? throw new AzureCliException("Runtime 版本响应异常。");
    }
}

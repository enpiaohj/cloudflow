using System.Diagnostics;
using System.Text;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// 统一的 Azure CLI 进程执行器（P0 Spike 步骤 2，规范 §16/§17）。
/// - 仅接受完整路径，禁止 PATH 依赖；
/// - AZURE_CONFIG_DIR 仅注入本子进程，未显式提供时强制清除，杜绝误用用户全局 Profile；
/// - 超时 / 取消时终止整个进程树（az.cmd → cmd.exe → python.exe）；
/// - 异常消息经 <see cref="AzureCliOutputRedactor"/> 脱敏，绝不携带 Token / Secret。
/// 全部 Azure CLI 调用必须经由本类，禁止业务代码直接 Process.Start("az", ...)。
/// </summary>
public sealed class AzureCliProcessRunner : IAzureCliProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<AzureCliResult> RunAsync(
        AzureCliInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocation.ExecutablePath) ||
            !Path.IsPathRooted(invocation.ExecutablePath))
        {
            throw new ArgumentException(
                "必须以完整路径调用 Azure CLI（禁止依赖 PATH）。", nameof(invocation));
        }

        if (!File.Exists(invocation.ExecutablePath))
        {
            throw new FileNotFoundException(
                "Azure CLI Runtime 不存在或已损坏，请通过 CloudFlow 修复。", invocation.ExecutablePath);
        }

        var timeout = invocation.Timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = BuildCommandLine(invocation.ExecutablePath, invocation.Arguments),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(invocation.ExecutablePath)!
        };
        ApplyConfigDirectory(startInfo, invocation.ConfigDirectory);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new AzureCliException("Azure CLI 进程启动失败。");
        }

        try
        {
            var stdoutTask = ReadStreamAsync(process.StandardOutput, invocation, timeoutCts.Token).ConfigureAwait(false);
            var stderrTask = ReadStreamAsync(process.StandardError, invocation, timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new AzureCliResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            throw new AzureCliException(
                $"Azure CLI 执行超时（超过 {timeout.TotalSeconds:F1} 秒），进程树已清理。");
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// cmd /c 的安全引号构造："cmd" 首尾引号会被剥除，得到 "exe" arg1 arg2 的规范命令行。
    /// 参数由调用方白名单控制（仅 version/login/account 子命令），不接受任意用户输入拼接。
    /// </summary>
    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder("/c \"\"");
        builder.Append(executablePath);
        builder.Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ')
                .Append('"')
                .Append(argument.Replace("\"", "\\\""))
                .Append('"');
        }
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>逐行读取流：累积完整输出并逐行触发回调（设备码提示需实时到达 UI）。</summary>
    private static async Task<string> ReadStreamAsync(
        StreamReader reader,
        AzureCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            buffer.AppendLine(line);
            invocation.OnOutputLine?.Invoke(line);
        }

        return buffer.ToString();
    }

    private static void ApplyConfigDirectory(ProcessStartInfo startInfo, string? configDirectory)
    {
        if (configDirectory is null)
        {
            // 强制移除：即使 CloudFlow 自身进程继承了全局 AZURE_CONFIG_DIR，也不允许泄漏到子进程
            startInfo.Environment.Remove("AZURE_CONFIG_DIR");
            return;
        }

        Directory.CreateDirectory(configDirectory);
        startInfo.Environment["AZURE_CONFIG_DIR"] = configDirectory;
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 进程已自然退出
        }
    }
}

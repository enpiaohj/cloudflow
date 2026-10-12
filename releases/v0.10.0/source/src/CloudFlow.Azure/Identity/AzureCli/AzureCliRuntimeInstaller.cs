using System.Net.Http;
using System.Security.Cryptography;
using System.IO.Compression;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// 按需下载并解压 CloudFlow 托管的 Azure CLI Runtime（个人 Microsoft 账户登录依赖它）。
/// </summary>
/// <remarks>
/// 发布的单文件 EXE 从不携带这份 ~90MB 的 Runtime（会员打包体积暴涨，而多数用户只用工作/学校
/// 账户登录、根本用不到它），此前"应用将自动分发完整 Runtime"这句承诺一直没有代码支撑——
/// 任何人拿发布出去的 EXE 点"添加个人账户"都会直接报"Runtime 缺失或损坏"。这里补上：
/// 首次真正需要时才下载，装一次以后就一直能用。
///
/// 下载源、版本与 SHA-256 与 P0 Spike 记录的认证基线完全一致（见
/// <see cref="AzureCliRuntimeManager"/> 与 <c>AzureCliRuntimeSmokeTests.CertifiedVersion</c>）——
/// 官方 GitHub Release 的 x64 ZIP（内置私有 Python 运行时，不依赖系统 Python，不需要管理员权限）。
/// 刻意钉死具体版本而不是"下载最新版"：这个版本是唯一经过本项目全部身份验证步骤实测过的，
/// 换一个版本等于绕开了那一整轮验证。
///
/// 安全纪律：下载内容必须先校验 SHA-256 完全一致才会被解压/使用；校验失败直接报错并删除下载，
/// 绝不执行或加载未经校验的内容。
/// </remarks>
public sealed class AzureCliRuntimeInstaller
{
    /// <summary>与 AzureCliRuntimeSmokeTests.CertifiedVersion 保持同一个值。</summary>
    public const string CertifiedVersion = "2.90.0";

    private const string DownloadUrl =
        "https://github.com/Azure/azure-cli/releases/download/azure-cli-2.90.0/azure-cli-2.90.0-x64.zip";

    // 官方发布页公布值，P0 Spike 期间用本机 certutil 复核过一致（见私有工作区的验证记录）。
    private const string ExpectedSha256 = "c4ef59b14f0edd074427fd9981e57b0780965ccdcf6191c033fdf4b4361f33d7";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="httpClient">测试替身；生产环境用默认共享实例。</param>
    /// <param name="root">
    /// Runtime 与下载缓存的根目录；默认 <c>%LOCALAPPDATA%\CloudFlow\Runtime</c>——
    /// 与 <see cref="AzureCliProfileManager"/> 同一条纪律：不引用 CloudFlow.Data 项目，
    /// 直接按同一约定拼路径，避免跨层依赖。
    /// </param>
    public AzureCliRuntimeInstaller(HttpClient? httpClient = null, string? root = null)
    {
        _http = httpClient ?? DefaultHttp;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudFlow", "Runtime");
    }

    /// <summary>解压后的 Runtime 目录；与 <see cref="AzureCliRuntimeManager.ResolveAzCmd"/> 的
    /// 探测路径保持一致（该方法已把这个目录加入候选列表）。</summary>
    public string InstallDirectory => Path.Combine(_root, "AzureCLI");

    public string AzCmdPath => Path.Combine(InstallDirectory, "bin", "az.cmd");

    public bool IsInstalled => File.Exists(AzCmdPath);

    /// <summary>
    /// 确保 Runtime 已就绪：已安装直接返回；否则下载、校验、解压。并发调用只真正跑一次——
    /// 第二个调用者等第一个跑完，不会同时下载两份、也不会在解压过程中被另一个调用者读到半份文件。
    /// </summary>
    public async Task EnsureInstalledAsync(Action<string>? onProgress, CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
            {
                return;
            }

            await DownloadAndExtractAsync(onProgress, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DownloadAndExtractAsync(Action<string>? onProgress, CancellationToken ct)
    {
        var downloadDir = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, "azure-cli-2.90.0-x64.zip");

        // 已经下载过且哈希仍然对：跳过重新下载——比如上次解压中途被取消，重试不必再拉一遍 90MB。
        var alreadyValid = File.Exists(zipPath) && await VerifyFileHashAsync(zipPath, ExpectedSha256, ct)
            .ConfigureAwait(false);
        if (!alreadyValid)
        {
            onProgress?.Invoke("正在下载 Azure CLI Runtime（约 90 MB，仅首次需要，用于个人账户登录）…");
            await DownloadWithVerificationAsync(zipPath, ct).ConfigureAwait(false);
        }

        onProgress?.Invoke("正在准备登录组件…");
        await ExtractAsync(zipPath, ct).ConfigureAwait(false);

        if (!IsInstalled)
        {
            throw new AzureCliException(
                "Azure CLI Runtime 解压后未找到 az.cmd，安装未成功，请检查磁盘空间后重试。");
        }
    }

    private async Task DownloadWithVerificationAsync(string zipPath, CancellationToken ct)
    {
        var tempPath = zipPath + ".tmp";
        try
        {
            using (var response = await _http
                       .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new AzureCliException(
                        $"下载 Azure CLI Runtime 失败：HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dest = File.Create(tempPath);
                await source.CopyToAsync(dest, ct).ConfigureAwait(false);
            }

            if (!await VerifyFileHashAsync(tempPath, ExpectedSha256, ct).ConfigureAwait(false))
            {
                throw new AzureCliException(
                    "下载的 Azure CLI Runtime 校验失败（SHA-256 与官方发布不一致），已放弃安装—— "
                    + "不会解压或执行任何未经校验的内容。请检查网络环境（如企业代理是否篡改了下载内容）后重试。");
            }

            File.Move(tempPath, zipPath, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureCliException($"下载 Azure CLI Runtime 失败，请检查网络连接：{ex.Message}", innerException: ex);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 最佳努力清理临时文件；删不掉不影响下次重试（重试会用新的 .tmp 覆盖）。
                }
            }
        }
    }

    /// <summary>独立成静态方法，不依赖网络/固定路径，方便单元测试用任意已知内容 + 已知哈希直接验证。</summary>
    public static async Task<bool> VerifyFileHashAsync(string path, string expectedHexSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var hex = Convert.ToHexString(hash);
        return string.Equals(hex, expectedHexSha256, StringComparison.OrdinalIgnoreCase);
    }

    private Task ExtractAsync(string zipPath, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(InstallDirectory))
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }

        Directory.CreateDirectory(InstallDirectory);
        ZipFile.ExtractToDirectory(zipPath, InstallDirectory);
    }, ct);
}

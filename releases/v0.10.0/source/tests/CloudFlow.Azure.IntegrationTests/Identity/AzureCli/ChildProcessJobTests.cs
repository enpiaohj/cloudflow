using System.Diagnostics;
using CloudFlow.Azure.Identity.AzureCli;
using Xunit;

namespace CloudFlow.Azure.IntegrationTests.Identity.AzureCli;

/// <summary>
/// 子进程 Job Object 绑定（防 CloudFlow 被强杀后遗留 az login 孤儿进程）。
/// 关键点：绑定必须真的成功——P/Invoke 结构体布局不正确会让
/// SetInformationJobObject 静默失败，加固就变成空操作。
/// </summary>
public sealed class ChildProcessJobTests
{
    [Fact]
    public void TryAttach_对已启动进程成功绑定Job对象()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 6 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;

        var jobHandle = ChildProcessJob.TryAttach(process);

        Assert.NotEqual(IntPtr.Zero, jobHandle);
        ChildProcessJob.Close(jobHandle);
        KillIfRunning(process);
    }

    [Fact]
    public void Close_空句柄为安全空操作()
    {
        ChildProcessJob.Close(IntPtr.Zero);
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // 已自然退出
        }
    }
}

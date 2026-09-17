using CloudFlow.Modules.Compute.Models;

namespace CloudFlow.App.Infrastructure;

/// <summary>
/// 页面间导航契约：ViewModel 之间不互相引用，统一经 Shell 导航。
/// </summary>
public interface IShellNavigation
{
    void NavigateHome();

    void NavigateVirtualMachines(string? filter = null);

    void NavigateToVmDetail(VmSummary vm);

    void NavigateJobs();
}

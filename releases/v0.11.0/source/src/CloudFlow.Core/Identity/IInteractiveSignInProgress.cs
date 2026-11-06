namespace CloudFlow.Core.Identity;

/// <summary>
/// 可在登录过程中向 UI 推送进度提示的身份 Provider 能力。
/// 典型用途：个人 Microsoft 账户的设备码登录 —— CLI 会输出设备码与验证网址，
/// UI 必须能在登录进行中实时展示，而不是等登录结束后才可见。
/// 提示内容仅为登录指引文本，其中不得包含 Token、密码或其他凭据。
/// </summary>
public interface IInteractiveSignInProgress
{
    Task<CloudAccount> SignInAsync(
        Action<string> onSignInMessage,
        CancellationToken cancellationToken = default);
}

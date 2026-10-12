using System.Text.Json;
using Azure;

namespace CloudFlow.Azure.Arm;

/// <summary>
/// 把 <see cref="RequestFailedException"/> 摘成一句人能读的话。
///
/// SDK 抛出的 <c>ex.Message</c> 是完整诊断转储（Status / ErrorCode / 原始 Content JSON / 全部
/// HTTP Header），直接怼给终端用户等于让人自己去读一份 HTTP 抓包。真正有用的一句话在响应体的
/// ARM 错误信封里（<c>{"error":{"code":"...","message":"..."}}</c>），这里从
/// <see cref="RequestFailedException.GetRawResponse"/> 拿到的结构化 Content 直接解，
/// 不去正则解析 <c>ex.Message</c> 这种非结构化文本。
/// </summary>
public static class AzureErrorMessages
{
    public static string Summarize(RequestFailedException ex)
    {
        var body = ex.GetRawResponse()?.Content;
        if (body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var messageProp) &&
                    messageProp.ValueKind == JsonValueKind.String)
                {
                    var code = error.TryGetProperty("code", out var codeProp) &&
                               codeProp.ValueKind == JsonValueKind.String
                        ? codeProp.GetString()
                        : ex.ErrorCode;
                    var message = messageProp.GetString();
                    return string.IsNullOrEmpty(code) ? message! : $"{code}：{message}";
                }
            }
            catch (JsonException)
            {
                // Body 不是预期的 ARM 错误信封形状（比如网关层直接返回的纯文本错误），退回下面的兜底。
            }
        }

        return string.IsNullOrEmpty(ex.ErrorCode) ? $"HTTP {ex.Status}" : $"HTTP {ex.Status}（{ex.ErrorCode}）";
    }
}

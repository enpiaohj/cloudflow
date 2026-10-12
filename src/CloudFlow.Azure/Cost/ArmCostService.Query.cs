using System.Globalization;
using System.Net;
using System.Text.Json;
using CloudFlow.Azure.Arm;
using CloudFlow.Core.Scopes;

namespace CloudFlow.Azure.Cost;

public sealed partial class ArmCostService
{
    public async Task<CostReport> GetCostsAsync(ResourceScope scope, DateOnly from, DateOnly to, bool byResource = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if(from>to || to>DateOnly.FromDateTime(DateTime.UtcNow)) throw new ArgumentException("日期范围无效，结束日期不能晚于今天（UTC）。");
        var account=scopeContext.ActiveAccount;
        if(account is null) return new([], ["请登录 Azure 后查看真实成本数据。"], DateTimeOffset.UtcNow);
        var ids=scope.SubscriptionIds.Count>0 ? scope.SubscriptionIds : scope.Mode is ScopeMode.AllAccessible or ScopeMode.AllAccounts
            ? scopeContext.AvailableSubscriptions.Select(s=>s.SubscriptionId).ToArray() : [];
        if(ids.Count==0) return new([], ["当前 Scope 没有已解析的订阅，无法查询成本。"], DateTimeOffset.UtcNow);
        // 在第一次 await 前固定每个订阅的认证上下文，防止账户切换串用令牌。
        var contexts=ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id=>(Id:id, Context:ActiveAccountContext.Create(account,scopeContext,id))).ToArray();
        var body=BuildQueryBody(from,to,byResource);
        List<ResourceCost> rows=[]; List<string> failures=[];
        foreach(var target in contexts)
        {
            ct.ThrowIfCancellationRequested();
            List<ResourceCost> subscriptionRows=[];
            try
            {
                var token=await tokenProvider.GetAsync(target.Context,ct).ConfigureAwait(false);
                var endpoint=$"https://management.azure.com/subscriptions/{Uri.EscapeDataString(target.Id)}/providers/Microsoft.CostManagement/query";
                string? url=endpoint+"?api-version="+ApiVersion;
                var visited=new HashSet<string>(StringComparer.Ordinal);
                while(url is not null)
                {
                    if(!visited.Add(url)) throw new InvalidDataException("成本响应重复分页链接，查询已停止。");
                    using var response=await SendCostPageAsync(url,token,body,ct).ConfigureAwait(false);
                    if(!response.IsSuccessStatusCode)
                    {
                        var reason=response.StatusCode switch { HttpStatusCode.Forbidden=>"没有 Cost Management 读取权限（403）。", HttpStatusCode.TooManyRequests=>"Cost Management 正在限流（429），稍后重试。", _=>$"成本查询失败（{(int)response.StatusCode}）。" };
                        throw new InvalidDataException(reason);
                    }
                    if (response.StatusCode == HttpStatusCode.NoContent) break;
                    var json=await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var page=ParseCostPage(json,target.Id,byResource);
                    subscriptionRows.AddRange(page.Rows);
                    url=ValidateNextLink(page.NextLink,endpoint);
                }
                rows.AddRange(subscriptionRows); // 仅完整订阅结果进入汇总，分页失败不能冒充完整账单。
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) { failures.Add($"订阅 {target.Id}：{ex.Message}"); }
        }
        var grouped=rows.GroupBy(r=>(r.SubscriptionId,r.ResourceId,r.ResourceGroup,r.Currency))
            .Select(g=>g.First() with { Amount=g.Sum(r=>r.Amount) }).OrderBy(r=>r.Currency).ThenByDescending(r=>r.Amount).ToArray();
        return new(grouped,failures,DateTimeOffset.UtcNow);
    }
    private static string BuildQueryBody(DateOnly from, DateOnly to, bool byResource) => JsonSerializer.Serialize(new
    {
        type="ActualCost",timeframe="Custom",
        timePeriod=new { from=from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"T00:00:00Z",to=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"T23:59:59Z" },
        dataset=new { granularity="None",aggregation=new { totalCost=new { name="Cost",function="Sum" } },
            grouping=byResource ? new[]{new {type="Dimension",name="ResourceId"},new {type="Dimension",name="ResourceGroup"}} : [] }
    });
    private static async Task<System.Net.Http.HttpResponseMessage> SendCostPageAsync(string url,string token,string body,CancellationToken ct)
    {
        for(var attempt=0;;attempt++)
        {
            var response=await SendAsync(url,token,body,ct).ConfigureAwait(false);
            if(response.StatusCode!=HttpStatusCode.TooManyRequests || attempt>=RetryDelaysSeconds.Length) return response;
            var delay=response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(RetryDelaysSeconds[attempt]);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds,1,120)),ct).ConfigureAwait(false);
        }
    }
    private static string? ValidateNextLink(string? link,string endpoint)
    {
        if(string.IsNullOrWhiteSpace(link)) return null;
        if(!Uri.TryCreate(link,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="management.azure.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.Equals(uri.AbsolutePath,new Uri(endpoint).AbsolutePath,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("成本响应包含不合法的分页地址。");
        return uri.AbsoluteUri;
    }
    private static (ResourceCost[] Rows,string? NextLink) ParseCostPage(string json,string subscriptionId,bool byResource)
    {
        using var doc=JsonDocument.Parse(json);
        var props=doc.RootElement.GetProperty("properties");
        var columns=props.GetProperty("columns").EnumerateArray().Select((c,i)=>(Name:c.GetProperty("name").GetString()!,Index:i)).ToDictionary(c=>c.Name,c=>c.Index,StringComparer.OrdinalIgnoreCase);
        if(!columns.TryGetValue("Cost",out var cost) && !columns.TryGetValue("PreTaxCost",out cost)) throw new InvalidDataException("成本响应缺少金额列。");
        if(!columns.TryGetValue("Currency",out var currency)) throw new InvalidDataException("成本响应缺少币种列。");
        if(byResource && !columns.ContainsKey("ResourceId")) throw new InvalidDataException("成本响应缺少资源列。");
        List<ResourceCost> result=[];
        foreach(var row in props.GetProperty("rows").EnumerateArray())
        {
            var cells=row.EnumerateArray().ToArray();
            if(cost>=cells.Length || currency>=cells.Length || (byResource && columns["ResourceId"] >= cells.Length)) throw new InvalidDataException("成本响应字段不完整。");
            decimal amount;
            if(cells[cost].ValueKind==JsonValueKind.Number) { if(!cells[cost].TryGetDecimal(out amount)) throw new InvalidDataException("成本金额无效。"); }
            else if(cells[cost].ValueKind!=JsonValueKind.String || !decimal.TryParse(cells[cost].GetString(),NumberStyles.Number,CultureInfo.InvariantCulture,out amount)) throw new InvalidDataException("成本金额无效。");
            var code=cells[currency].GetString(); if(string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("成本币种无效。");
            string Text(string column) => columns.TryGetValue(column,out var index) && index<cells.Length && cells[index].ValueKind==JsonValueKind.String ? cells[index].GetString() ?? "" : "";
            result.Add(new(subscriptionId,Text("ResourceId"),Text("ResourceGroup"),amount,code));
        }
        var next=props.TryGetProperty("nextLink",out var nextValue) && nextValue.ValueKind==JsonValueKind.String ? nextValue.GetString() : null;
        return (result.ToArray(),next);
    }
}

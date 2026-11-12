using System.Text.Json.Serialization.Metadata;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 端 JSON 原始碼產生器的設定（1.1.37）。
///
/// <para>
/// 1.1.37 Token 用量頁曾整頁變成錯誤畫面：<c>ApiResult&lt;AiUsagePagedResult&gt;</c> 取了別名
/// <c>TypeInfoPropertyName = "AiUsagePagedResult"</c>，跟產生器替內層型別 <c>AiUsagePagedResult</c> 自動取的名字撞名，
/// 內層型別就沒有中繼資料（執行期 <c>NotSupportedException: NoMetadataForType</c>）。建置照樣成功，只有實際讀到那支 API 才炸。
/// 這裡把每一個型別資訊都拿一次，撞名或漏註冊在單元測試就會失敗。
/// </para>
/// </summary>
public sealed class ClientJsonContextTests
{
    [Fact]
    public void EveryRegisteredType_ShouldResolveItsMetadata()
    {
        var failures = new List<string>();
        foreach (var property in typeof(ClientJsonContext).GetProperties()
                     .Where(x => typeof(JsonTypeInfo).IsAssignableFrom(x.PropertyType)))
        {
            try
            {
                var info = (JsonTypeInfo)property.GetValue(ClientJsonContext.Default)!;
                foreach (var member in info.Properties)
                {
                    _ = ClientJsonContext.Default.GetTypeInfo(member.PropertyType)
                        ?? throw new InvalidOperationException($"{member.Name}（{member.PropertyType.Name}）沒有中繼資料");
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Reflection.TargetInvocationException)
            {
                failures.Add($"{property.Name}：{(ex.InnerException ?? ex).Message}");
            }
        }

        Assert.True(failures.Count == 0, "ClientJsonContext 有型別拿不到中繼資料（多半是 TypeInfoPropertyName 跟型別名稱撞名）：\n" + string.Join("\n", failures));
    }
}

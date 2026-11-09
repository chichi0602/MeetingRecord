using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Sessions;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// WASM 端的 JSON 序列化一律走原始碼產生器（1.1.1）：反射式序列化在修剪後會缺型別資訊而在執行期出錯。
/// 伺服器用 ASP.NET Core 預設（camelCase），這裡用同一組 Web 預設。新 API 的回應型別要加在這裡。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ApiResult<SessionDto>), TypeInfoPropertyName = "SessionResult")]
[JsonSerializable(typeof(ApiResult<List<MenuItemDto>>), TypeInfoPropertyName = "MenuResult")]
public sealed partial class ClientJsonContext : JsonSerializerContext;

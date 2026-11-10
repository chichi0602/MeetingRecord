using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Dtos.Systems;
using MeetingRecord.Dtos.Users;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// WASM 端的 JSON 序列化一律走原始碼產生器（1.1.1）：反射式序列化在修剪後會缺型別資訊而在執行期出錯。
/// 伺服器用 ASP.NET Core 預設（camelCase），這裡用同一組 Web 預設。新 API 的回應型別要加在這裡。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ApiResult<SessionDto>), TypeInfoPropertyName = "SessionResult")]
[JsonSerializable(typeof(ApiResult<List<MenuItemDto>>), TypeInfoPropertyName = "MenuResult")]
[JsonSerializable(typeof(ApiResult<int>), TypeInfoPropertyName = "IntResult")]
[JsonSerializable(typeof(ApiResult<object>), TypeInfoPropertyName = "EmptyResult")]
[JsonSerializable(typeof(ApiResult<SystemAboutDto>), TypeInfoPropertyName = "AboutResult")]
[JsonSerializable(typeof(ChangePasswordRequestDto))]
[JsonSerializable(typeof(PageDeniedRequestDto))]
[JsonSerializable(typeof(ApiResult<List<NotificationDto>>), TypeInfoPropertyName = "NotificationsResult")]
[JsonSerializable(typeof(ApiResult<PagedResult<CategoryDto>>), TypeInfoPropertyName = "CategoryPageResult")]
[JsonSerializable(typeof(ApiResult<CategoryDto>), TypeInfoPropertyName = "CategoryResult")]
[JsonSerializable(typeof(ApiResult<List<TeamOptionDto>>), TypeInfoPropertyName = "TeamOptionsResult")]
[JsonSerializable(typeof(CategorySearchRequestDto))]
[JsonSerializable(typeof(CategoryCreateUpdateDto))]
[JsonSerializable(typeof(SetEnabledRequestDto))]
[JsonSerializable(typeof(ApiResult<PagedResult<TeamDto>>), TypeInfoPropertyName = "TeamPageResult")]
[JsonSerializable(typeof(ApiResult<TeamDto>), TypeInfoPropertyName = "TeamResult")]
[JsonSerializable(typeof(ApiResult<List<MemberOptionDto>>), TypeInfoPropertyName = "MemberOptionsResult")]
[JsonSerializable(typeof(TeamSearchRequestDto))]
[JsonSerializable(typeof(TeamCreateUpdateDto))]
[JsonSerializable(typeof(ApiResult<PagedResult<PromptTemplateDto>>), TypeInfoPropertyName = "PromptTemplatePageResult")]
[JsonSerializable(typeof(ApiResult<PromptTemplateDto>), TypeInfoPropertyName = "PromptTemplateResult")]
[JsonSerializable(typeof(ApiResult<List<string>>), TypeInfoPropertyName = "StringListResult")]
[JsonSerializable(typeof(PromptTemplateSearchRequestDto))]
[JsonSerializable(typeof(PromptTemplateCreateUpdateDto))]
[JsonSerializable(typeof(PresetNameRequestDto))]
[JsonSerializable(typeof(ApiResult<PagedResult<UserDto>>), TypeInfoPropertyName = "UserPageResult")]
[JsonSerializable(typeof(ApiResult<UserDto>), TypeInfoPropertyName = "UserResult")]
[JsonSerializable(typeof(ApiResult<List<RoleOptionDto>>), TypeInfoPropertyName = "RoleOptionsResult")]
[JsonSerializable(typeof(UserSearchRequestDto))]
[JsonSerializable(typeof(UserCreateUpdateDto))]
[JsonSerializable(typeof(UserTeamBatchRequestDto))]
public sealed partial class ClientJsonContext : JsonSerializerContext;

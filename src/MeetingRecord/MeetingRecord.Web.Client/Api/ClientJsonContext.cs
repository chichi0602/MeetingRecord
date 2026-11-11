using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Dashboards;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Dtos.Helps;
using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Dtos.Projects;
using MeetingRecord.Dtos.Roles;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Dtos.Systems;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Dtos.Uploads;
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
[JsonSerializable(typeof(ApiResult<PagedResult<RoleDto>>), TypeInfoPropertyName = "RolePageResult")]
[JsonSerializable(typeof(ApiResult<RoleDto>), TypeInfoPropertyName = "RoleResult")]
[JsonSerializable(typeof(ApiResult<RolePermissionDto>), TypeInfoPropertyName = "RolePermissionResult")]
[JsonSerializable(typeof(RoleSearchRequestDto))]
[JsonSerializable(typeof(RoleDto))]
[JsonSerializable(typeof(ApiResult<DashboardSummary>), TypeInfoPropertyName = "DashboardResult")]
[JsonSerializable(typeof(OwnPasswordRequestDto))]
[JsonSerializable(typeof(ApiResult<PasswordStatusDto>), TypeInfoPropertyName = "PasswordStatusResult")]
[JsonSerializable(typeof(ApiResult<List<BackgroundJobDto>>), TypeInfoPropertyName = "BackgroundJobsResult")]
[JsonSerializable(typeof(TodoTreeRequestDto))]
[JsonSerializable(typeof(ApiResult<List<AiChatConversationDto>>), TypeInfoPropertyName = "AiChatConversationsResult")]
[JsonSerializable(typeof(ApiResult<List<AiChatMessageDto>>), TypeInfoPropertyName = "AiChatMessagesResult")]
[JsonSerializable(typeof(ApiResult<string>), TypeInfoPropertyName = "StringResult")]
[JsonSerializable(typeof(AiChatRenameRequestDto))]
[JsonSerializable(typeof(AiChatUpdateMessageRequestDto))]
[JsonSerializable(typeof(AiChatRegenerateRequestDto))]
[JsonSerializable(typeof(AiChatExportRequestDto))]
[JsonSerializable(typeof(AiChatStreamEventDto))]
[JsonSerializable(typeof(ApiResult<HelpManualDto>), TypeInfoPropertyName = "HelpManualResult")]
[JsonSerializable(typeof(HelpAssistantAskRequestDto))]
[JsonSerializable(typeof(HelpAssistantStreamEventDto))]
[JsonSerializable(typeof(ApiResult<DatabaseUsageReport>), TypeInfoPropertyName = "DatabaseUsageResult")]
[JsonSerializable(typeof(ApiResult<StorageUsageReport>), TypeInfoPropertyName = "StorageUsageResult")]
[JsonSerializable(typeof(ApiResult<LogLevelStateDto>), TypeInfoPropertyName = "LogLevelStateResult")]
[JsonSerializable(typeof(LogLevelApplyRequestDto))]
[JsonSerializable(typeof(ApiResult<SystemHealthReport>), TypeInfoPropertyName = "SystemHealthResult")]
[JsonSerializable(typeof(ApiResult<LogQueryResult>), TypeInfoPropertyName = "LogQueryResultResult")]
[JsonSerializable(typeof(ApiResult<List<AiConnectionProbeResult>>), TypeInfoPropertyName = "AiConnectionProbeResults")]
[JsonSerializable(typeof(TodoCreateUpdateDto))]
[JsonSerializable(typeof(TodoCompletedRequestDto))]
[JsonSerializable(typeof(ApiResult<PagedResult<TodoDto>>), TypeInfoPropertyName = "TodoPageResult")]
[JsonSerializable(typeof(ApiResult<TodoDto>), TypeInfoPropertyName = "TodoResult")]
[JsonSerializable(typeof(ApiResult<List<TodoDto>>), TypeInfoPropertyName = "TodoListResult")]
[JsonSerializable(typeof(ApiResult<List<TodoOwnerSummaryDto>>), TypeInfoPropertyName = "TodoOwnersResult")]
[JsonSerializable(typeof(ApiResult<List<TodoChangeDto>>), TypeInfoPropertyName = "TodoChangesResult")]
[JsonSerializable(typeof(ApiResult<List<TodoParentOptionDto>>), TypeInfoPropertyName = "TodoParentOptionsResult")]
[JsonSerializable(typeof(ApiResult<List<TodoProjectOptionDto>>), TypeInfoPropertyName = "TodoProjectOptionsResult")]
[JsonSerializable(typeof(MeetingListRequestDto))]
[JsonSerializable(typeof(MeetingCreateUpdateDto))]
[JsonSerializable(typeof(MeetingDraftRequestDto))]
[JsonSerializable(typeof(MeetingDraftContentDto))]
[JsonSerializable(typeof(MeetingAttachRequestDto))]
[JsonSerializable(typeof(MeetingPdfRequestDto))]
[JsonSerializable(typeof(ApiResult<PagedResult<MeetingDto>>), TypeInfoPropertyName = "MeetingPageResult")]
[JsonSerializable(typeof(ApiResult<MeetingDto>), TypeInfoPropertyName = "MeetingResult")]
[JsonSerializable(typeof(ApiResult<MeetingTranscriptDto>), TypeInfoPropertyName = "MeetingTranscriptResult")]
[JsonSerializable(typeof(ApiResult<List<MeetingProjectOptionDto>>), TypeInfoPropertyName = "MeetingProjectOptionsResult")]
[JsonSerializable(typeof(ApiResult<List<MeetingPromptOptionDto>>), TypeInfoPropertyName = "MeetingPromptOptionsResult")]
[JsonSerializable(typeof(UploadSessionRequestDto))]
[JsonSerializable(typeof(UploadFailRequestDto))]
[JsonSerializable(typeof(ApiResult<UploadSessionDto>), TypeInfoPropertyName = "UploadSessionResult")]
[JsonSerializable(typeof(ApiResult<UploadStatusDto>), TypeInfoPropertyName = "UploadStatusResult")]
[JsonSerializable(typeof(ApiResult<List<ProjectDto>>), TypeInfoPropertyName = "ProjectListResult")]
[JsonSerializable(typeof(ApiResult<ProjectDto>), TypeInfoPropertyName = "ProjectResult")]
[JsonSerializable(typeof(ProjectFormDto))]
[JsonSerializable(typeof(ApiResult<ProjectSaveResultDto>), TypeInfoPropertyName = "ProjectSaveResult")]
[JsonSerializable(typeof(LastSelectedProjectDto))]
[JsonSerializable(typeof(ApiResult<LastSelectedProjectDto>), TypeInfoPropertyName = "LastSelectedProjectResult")]
[JsonSerializable(typeof(ApiResult<List<ProjectOwnerCandidateDto>>), TypeInfoPropertyName = "ProjectOwnerCandidatesResult")]
[JsonSerializable(typeof(ApiResult<List<MeetingDto>>), TypeInfoPropertyName = "MeetingListResult")]
[JsonSerializable(typeof(ApiResult<List<ProjectTranscriptOptionDto>>), TypeInfoPropertyName = "ProjectTranscriptOptionsResult")]
[JsonSerializable(typeof(ApiResult<TodoExtractionResultDto>), TypeInfoPropertyName = "TodoExtractionResult")]
[JsonSerializable(typeof(ExtractedTodoAddRequestDto))]
[JsonSerializable(typeof(ExtractedTodoUpdateRequestDto))]
public sealed partial class ClientJsonContext : JsonSerializerContext;

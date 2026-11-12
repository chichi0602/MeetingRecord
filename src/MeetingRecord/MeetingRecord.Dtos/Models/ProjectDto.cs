using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 專案資料傳輸物件
/// </summary>
public class ProjectDto : ProjectCreateUpdateDto
{
    public ProjectDto()
    {
    }

    // ---- 1.1.24 起多帶的唯讀欄位：只加不改，舊的呼叫端照用 ----

    [JsonPropertyName("primaryTeamName")]
    public string PrimaryTeamName { get; set; } = string.Empty;

    [JsonPropertyName("collaboratorTeamNames")]
    public List<string> CollaboratorTeamNames { get; set; } = [];

    /// <summary>已完成（封存）：非管理者唯讀。</summary>
    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    /// <summary>附件（清單不帶，<c>GET {id}</c> 才帶）。</summary>
    [JsonPropertyName("files")]
    public List<ProjectFileDto> Files { get; set; } = [];

    /// <summary>呼叫者是專案負責人（或管理者）：刪專案、刪附件只限這些人。只給畫面決定按鈕，真正的檢查在伺服器。</summary>
    [JsonPropertyName("isProjectOwner")]
    public bool IsProjectOwner { get; set; }

    /// <summary>呼叫者能改團隊（主責團隊成員或管理者）。</summary>
    [JsonPropertyName("canManageTeams")]
    public bool CanManageTeams { get; set; }

    /// <summary>呼叫者能換負責人（主責團隊成員、目前的負責人或管理者）。</summary>
    [JsonPropertyName("canChangeOwner")]
    public bool CanChangeOwner { get; set; }
}

/// <summary>專案附件（1.1.24）。下載走 <c>GET api/v1/Project/files/{id}</c>。</summary>
public class ProjectFileDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("originalFileName")]
    public string OriginalFileName { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

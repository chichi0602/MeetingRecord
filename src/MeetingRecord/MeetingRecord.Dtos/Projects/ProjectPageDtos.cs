namespace MeetingRecord.Dtos.Projects;

/// <summary>
/// 專案項目頁的表單存檔（<c>POST api/v1/Project/form</c>、<c>PUT api/v1/Project/{id}/form</c>，1.1.24）。
/// 與對外的 <c>POST</c>／<c>PUT</c> 不同：整筆覆寫（名單傳空就清空）、日期可留空、可帶附件，規則在服務層。
/// </summary>
public class ProjectFormDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public int CompletionPercentage { get; set; }

    public int? OwnerUserId { get; set; }

    public int? PrimaryTeamId { get; set; }

    public List<int> CollaboratorTeamIds { get; set; } = [];

    public List<string> Categories { get; set; } = [];

    public List<string> GlossaryTerms { get; set; } = [];

    public List<string> Participants { get; set; } = [];

    /// <summary>新加的附件：先用 <c>POST api/v1/uploads/project-attachments</c> 傳完，再把編號帶進來。</summary>
    public List<Guid> UploadIds { get; set; } = [];

    /// <summary>要移除的附件（只限專案負責人）。</summary>
    public List<int> RemovedFileIds { get; set; } = [];
}

/// <summary>
/// 表單存檔結果。新增時專案已建立、但附件存不進去，回 400 也會帶 <see cref="ProjectId"/>：
/// 畫面要切成修改模式重送附件，不然再按一次會多建一筆。
/// </summary>
public class ProjectSaveResultDto
{
    public int ProjectId { get; set; }
}

/// <summary>專案項目頁上次選的專案（記在帳號上，1.0.51）。</summary>
public class LastSelectedProjectDto
{
    public int? ProjectId { get; set; }
}

/// <summary>負責人下拉的候選人（專案團隊裡啟用中的帳號，1.0.52）。</summary>
public class ProjectOwnerCandidateDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Account { get; set; } = string.Empty;
}

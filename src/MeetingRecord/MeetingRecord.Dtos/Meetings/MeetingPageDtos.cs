namespace MeetingRecord.Dtos.Meetings;

/// <summary>
/// 會議清單頁的查詢（<c>POST api/v1/Meeting/list</c>，1.1.20）：分類篩選（任一）、畫面欄位排序（Title、MeetingDate、
/// TranscriptionStatus、CreatedAt、UpdatedAt）。與對外的 <c>POST search</c> 條件不同，所以另開端點。
/// </summary>
public class MeetingListRequestDto
{
    public string? Keyword { get; set; }

    public List<string> CategoryFilters { get; set; } = [];

    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 10;

    public string? SortBy { get; set; }

    /// <summary>null＝不排序（用預設）。</summary>
    public bool? SortDescending { get; set; }
}

/// <summary>產生（或重新產生）會議紀錄（<c>POST api/v1/Meeting/{id}/draft</c>）。會花 AI 費用，額度與用量由伺服器把關。</summary>
public class MeetingDraftRequestDto
{
    public int PromptTemplateId { get; set; }

    /// <summary>順便歸屬到這個專案；已歸屬的會議不得改歸屬（伺服器會擋）。</summary>
    public int? ProjectId { get; set; }

    /// <summary>本次到場的人（從專案的與會人員名冊勾選）。</summary>
    public List<string> Attendees { get; set; } = [];
}

/// <summary>修改會議紀錄全文（<c>PUT api/v1/Meeting/{id}/draft</c>）。</summary>
public class MeetingDraftContentDto
{
    public string Content { get; set; } = string.Empty;
}

/// <summary>把會議歸屬到專案（<c>PUT api/v1/Meeting/{id}/project</c>）。</summary>
public class MeetingAttachRequestDto
{
    public int ProjectId { get; set; }
}

/// <summary>
/// 匯出會議紀錄 PDF（<c>POST api/v1/Meeting/{id}/draft/pdf</c>）。帶 <see cref="Content"/> 就用它（編修中、還沒存的內容），
/// 沒帶就用資料庫裡的。
/// </summary>
public class MeetingPdfRequestDto
{
    public string? Content { get; set; }
}

/// <summary>逐字稿全文（<c>GET api/v1/Meeting/{id}/transcript</c>）。</summary>
public class MeetingTranscriptDto
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>「標示講者」的一位講者（1.1.64）：編號、目前對到的名字、幾句他說的話。</summary>
public class MeetingSpeakerDto
{
    /// <summary>整場對齊的編號（"1"～"4"），逐字稿裡是「講者 1：」。</summary>
    public string Label { get; set; } = string.Empty;

    public string? Name { get; set; }

    public List<string> Samples { get; set; } = [];

    /// <summary>1.1.71：有沒有試聽片段（<c>GET …/speakers/{label}/clip</c>）。重新轉錄前的舊逐字稿沒有。</summary>
    public bool HasClip { get; set; }
}

/// <summary>「標示講者」視窗的資料（<c>GET api/v1/Meeting/{id}/speakers</c>）。</summary>
public class MeetingSpeakersDto
{
    public List<MeetingSpeakerDto> Speakers { get; set; } = [];

    /// <summary>會議所屬專案的與會人員名冊，給名字欄位挑；未歸屬專案時是空的。</summary>
    public List<string> Participants { get; set; } = [];

    /// <summary>逐字稿有沒有字母代號（只在該段有效、不能對照）。</summary>
    public bool HasSegmentLabels { get; set; }

    /// <summary>1.1.69：轉錄完在等標示講者、存檔後要自動產生會議紀錄（視窗顯示「儲存並產生會議紀錄」）。</summary>
    public bool CanGenerate { get; set; }
}

/// <summary>儲存講者對照（<c>PUT api/v1/Meeting/{id}/speakers</c>）。鍵是編號，值留空代表不對照。</summary>
public class MeetingSpeakerNamesDto
{
    public Dictionary<string, string> Names { get; set; } = [];
}

/// <summary>會議頁可選的專案（產生會議紀錄、歸屬專案用），附與會人員名冊。</summary>
public class MeetingProjectOptionDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<string> Participants { get; set; } = [];
}

/// <summary>會議頁可選的提示詞（啟用中的）。</summary>
public class MeetingPromptOptionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 1.1.68 成本閘門：轉錄的預估費用。金額是新台幣；估不出來（沒單價、沒匯率）或額度不限制時為 null。
/// </summary>
public class MeetingTranscriptionEstimateDto
{
    public double Seconds { get; set; }

    /// <summary>預估轉錄費用。以秒按比例計，實際帳單可能以分鐘進位而略高。</summary>
    public decimal? EstimatedTwd { get; set; }

    /// <summary>達到這個金額要再確認；0＝不提醒。</summary>
    public int ConfirmThresholdTwd { get; set; }

    /// <summary>本月剩餘額度（全系統與自己兩者較少的那個）。不限制時為 null。</summary>
    public decimal? RemainingTwd { get; set; }
}

/// <summary>1.1.71：會議紀錄的一份舊版（清單用，不含內容）。</summary>
public class MeetingDraftVersionDto
{
    public int Id { get; set; }

    /// <summary>留版的時間。</summary>
    public DateTime SavedAt { get; set; }

    /// <summary>為什麼留這一版：「重新產生前」「編修前」「還原前」。</summary>
    public string ReasonText { get; set; } = string.Empty;

    /// <summary>那份內容當初用的提示詞。</summary>
    public string? PromptTemplateName { get; set; }

    /// <summary>造成這次覆蓋的人。</summary>
    public string? SavedByName { get; set; }

    /// <summary>那份內容原本產生或最後修改的時間。</summary>
    public DateTime? ContentAt { get; set; }

    /// <summary>字數。</summary>
    public int Length { get; set; }
}

/// <summary>1.1.71：會議紀錄某一份舊版的內容。</summary>
public class MeetingDraftVersionDetailDto : MeetingDraftVersionDto
{
    public string Content { get; set; } = string.Empty;

    public List<string> Attendees { get; set; } = [];
}

namespace MeetingRecord.Share.Enums;

/// <summary>
/// 專案成員的身分（0.4.99）。以 int 儲存於資料庫。
///
/// <para>
/// 一個專案**只有一位負責人**：可以編輯專案資料、加減協作者；協作者可以操作專案底下的
/// 會議紀錄、待辦與 AI 問答，但不能改專案資料與成員。刪除專案只有管理者能做，不在這裡。
/// </para>
/// </summary>
public enum ProjectMemberRole
{
    Owner = 0,

    Collaborator = 1,
}

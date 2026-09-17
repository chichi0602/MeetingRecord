namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 一筆待處理的背景工作：處理哪一場會議，以及**是誰按下去的**。
///
/// <para>
/// ⚠️ 使用者一定要在入列時就帶著走。背景服務為每筆工作自建 DI scope，而那個 scope 裡的
/// <see cref="CurrentUserService"/> 解析出來是一個**空白的** <c>CurrentUser</c>（Id=0、Name=""），
/// 不是 null 也不會拋例外——讓 runner 自己去問，結果是所有背景呼叫都被默默記到「空白使用者」，
/// 而用量分析頁上「誰在燒錢」就永遠是一片空白。
/// </para>
///
/// <para>
/// 轉錄與草稿生成兩條佇列共用同一個型別：兩者的 payload 完全一樣，
/// 分成兩個 record 只會讓 enqueue 端每次都要想「現在該用哪一個」。
/// </para>
/// </summary>
/// <param name="MeetingId">要處理的會議。</param>
/// <param name="RequestedByUserId">觸發者的使用者 Id；系統自動觸發時為 null。</param>
/// <param name="RequestedByUserName">觸發者姓名快照，供帳本在使用者改名或刪除後仍看得出是誰。</param>
public sealed record MeetingJobRequest(
    int MeetingId,
    int? RequestedByUserId = null,
    string? RequestedByUserName = null);

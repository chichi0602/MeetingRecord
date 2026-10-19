using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 已完成專案＝封存（1.0.46）的規則，Web 與 API 共用。
///
/// <para>
/// 使用者問「已完成專案要怎麼呈現（封存？）」，決定：已完成就是封存，不另加狀態；
/// 封存的專案一般使用者<b>唯讀</b>，要動就先「重新開啟」（狀態改回其他任一種）；<b>管理者不受限</b>（全站慣例）。
/// </para>
///
/// <para>
/// 擋的是「改專案內容」的路徑：待辦、會議紀錄的產生／歸屬／移除／編修、專案附件。
/// 不擋檢視、下載 PDF、AI 問答、異動紀錄，也不擋專案本身的編輯——改狀態就是重新開啟的方式。
/// 新增改專案內容的路徑時要記得呼叫 <see cref="CheckWritableAsync"/>。
/// </para>
/// </summary>
public static class ProjectArchiveRules
{
    public const string ArchivedMessage = "這個專案已完成（封存），請先重新開啟再修改。";

    /// <summary>可以改這個專案的內容嗎？可以回 null，否則回給使用者看的原因。專案不存在時回 null（交給呼叫端原本的檢查）。</summary>
    public static async Task<string?> CheckWritableAsync(BackendDBContext context, int? projectId, ProjectAccess access)
    {
        if (projectId is not { } id || access.IsAdmin)
        {
            return null;
        }

        var archived = await context.Project.AsNoTracking()
            .AnyAsync(x => x.Id == id && x.Status == ProjectAdapterModel.ArchivedStatus);

        return archived ? ArchivedMessage : null;
    }

    /// <summary>依檢視切換縮小專案範圍。</summary>
    public static IQueryable<Project> ApplyScope(IQueryable<Project> query, ProjectScope scope) => scope switch
    {
        ProjectScope.Active => query.Where(x => x.Status != ProjectAdapterModel.ArchivedStatus),
        ProjectScope.Archived => query.Where(x => x.Status == ProjectAdapterModel.ArchivedStatus),
        _ => query,
    };

    /// <summary>依檢視切換縮小待辦範圍（看待辦所屬專案的狀態）。</summary>
    public static IQueryable<Todo> ApplyScope(IQueryable<Todo> query, ProjectScope scope) => scope switch
    {
        ProjectScope.Active => query.Where(x => x.Project!.Status != ProjectAdapterModel.ArchivedStatus),
        ProjectScope.Archived => query.Where(x => x.Project!.Status == ProjectAdapterModel.ArchivedStatus),
        _ => query,
    };

    public static bool Matches(string? status, ProjectScope scope) => scope switch
    {
        ProjectScope.Active => status != ProjectAdapterModel.ArchivedStatus,
        ProjectScope.Archived => status == ProjectAdapterModel.ArchivedStatus,
        _ => true,
    };
}

using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Repositories;

/// <summary>
/// 會議紀錄的 Web API 資料存取（僅中繼資料的 CRUD）。
///
/// 0.4.99 起與 Blazor 的 <c>MeetingService</c> 套同一條專案權限（<see cref="ProjectAccessService"/>）：
/// 看不到的會議一律當成不存在（控制器回 404）。0.4.98 以前 API 完全不過濾。
///
/// 影音檔上傳、轉錄與逐字稿讀取都不經由這條路徑——那些操作牽涉實體檔案與背景佇列，
/// 只在 Blazor 服務層提供。
/// </summary>
public class MeetingRepository
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;

    public MeetingRepository(BackendDBContext context, ProjectAccessService projectAccess)
    {
        this.context = context;
        this.projectAccess = projectAccess;
    }

    #region 查詢方法

    public async Task<Meeting?> GetByIdAsync(int id)
    {
        return await (await projectAccess.GetAsync()).Filter(context.Meeting.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Meeting>> GetPagedAsync(MeetingSearchRequestDto request)
    {
        var query = (await projectAccess.GetAsync()).Filter(context.Meeting.AsNoTracking());

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Title.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)) ||
                (x.MediaOriginalFileName != null && x.MediaOriginalFileName.Contains(request.Keyword)));
        }

        if (request.TranscriptionStatus.HasValue)
        {
            var status = (TranscriptionStatus)request.TranscriptionStatus.Value;
            query = query.Where(x => x.TranscriptionStatus == status);
        }

        query = request.SortBy?.ToLower() switch
        {
            "title" => request.SortDescending ? query.OrderByDescending(x => x.Title) : query.OrderBy(x => x.Title),
            "meetingdate" => request.SortDescending ? query.OrderByDescending(x => x.MeetingDate) : query.OrderBy(x => x.MeetingDate),
            "transcriptionstatus" => request.SortDescending ? query.OrderByDescending(x => x.TranscriptionStatus) : query.OrderBy(x => x.TranscriptionStatus),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<Meeting>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Meeting> AddAsync(Meeting meeting)
    {
        // 一律由資料庫配號（0.4.115）：照抄用戶端的 Id，可以用來探測別人的會議 Id 是否存在，
        // 也能接手已刪除會議的 Id。
        meeting.Id = 0;
        meeting.CreatedAt = DateTime.Now;
        meeting.UpdatedAt = DateTime.Now;

        // API 建立的會議一律未歸屬，上傳者就是呼叫者——否則建完自己就看不到了。
        var access = await projectAccess.GetAsync();
        meeting.ProjectId = null;
        meeting.CreatedByUserId = access.UserId == 0 ? null : access.UserId;

        await context.Meeting.AddAsync(meeting);
        await context.SaveChangesAsync();

        return meeting;
    }

    /// <summary>
    /// 更新中繼資料。影音檔、轉錄與草稿欄位一律沿用既有值，
    /// 避免 API 用戶端覆寫背景轉錄與背景生成寫入的狀態。
    /// </summary>
    public async Task<bool> UpdateAsync(Meeting meeting)
    {
        var existing = await context.Meeting.FindAsync(meeting.Id);
        if (existing == null || !(await projectAccess.GetAsync()).CanViewMeeting(existing.ProjectId, existing.CreatedByUserId))
        {
            return false;
        }

        meeting.UpdatedAt = DateTime.Now;
        meeting.CreatedAt = existing.CreatedAt;
        meeting.MediaOriginalFileName = existing.MediaOriginalFileName;
        meeting.MediaStoredFileName = existing.MediaStoredFileName;
        meeting.MediaRelativePath = existing.MediaRelativePath;
        meeting.MediaContentType = existing.MediaContentType;
        meeting.MediaFileSize = existing.MediaFileSize;
        meeting.TranscriptRelativePath = existing.TranscriptRelativePath;
        meeting.TranscriptionStatus = existing.TranscriptionStatus;
        meeting.TranscriptionError = existing.TranscriptionError;
        meeting.TranscriptionStartedAt = existing.TranscriptionStartedAt;
        meeting.TranscriptionCompletedAt = existing.TranscriptionCompletedAt;
        meeting.DraftContent = existing.DraftContent;
        meeting.DraftStatus = existing.DraftStatus;
        meeting.DraftError = existing.DraftError;
        meeting.DraftPromptTemplateId = existing.DraftPromptTemplateId;
        meeting.DraftPromptTemplateName = existing.DraftPromptTemplateName;
        meeting.DraftStartedAt = existing.DraftStartedAt;
        meeting.DraftCompletedAt = existing.DraftCompletedAt;

        // DTO 沒有這兩欄，不沿用的話 SetValues 會把它們清成 null——
        // 0.4.98 以前 API 更新一次就會把會議從專案裡拔掉。
        meeting.ProjectId = existing.ProjectId;
        meeting.CreatedByUserId = existing.CreatedByUserId;
        meeting.DraftAttendees = existing.DraftAttendees;

        context.Entry(existing).CurrentValues.SetValues(meeting);
        await context.SaveChangesAsync();

        return true;
    }

    // 刪除不放在這裡（0.4.115）：一律走 MeetingService.DeleteAsync，才會一併清掉實體檔與 AI 問答對話。

    #endregion
}

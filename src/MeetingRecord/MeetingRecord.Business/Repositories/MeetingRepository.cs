using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Repositories;

/// <summary>
/// 會議紀錄的 Web API 資料存取（僅中繼資料的 CRUD）。
///
/// 與 <c>ProjectRepository</c>、<c>PromptTemplateRepository</c> 一致，API（repository）路徑
/// 不做團隊行級過濾；團隊可見性只在 Blazor 的 <c>MeetingService</c> 生效。
///
/// 影音檔上傳、轉錄與逐字稿讀取都不經由這條路徑——那些操作牽涉實體檔案與背景佇列，
/// 只在 Blazor 服務層提供。
/// </summary>
public class MeetingRepository
{
    private readonly BackendDBContext context;

    public MeetingRepository(BackendDBContext context)
    {
        this.context = context;
    }

    #region 查詢方法

    public async Task<Meeting?> GetByIdAsync(int id)
    {
        return await context.Meeting.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Meeting>> GetPagedAsync(MeetingSearchRequestDto request)
    {
        var query = context.Meeting.AsNoTracking().AsQueryable();

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
        meeting.CreatedAt = DateTime.Now;
        meeting.UpdatedAt = DateTime.Now;

        await context.Meeting.AddAsync(meeting);
        await context.SaveChangesAsync();

        return meeting;
    }

    /// <summary>
    /// 更新中繼資料。影音檔與轉錄欄位一律沿用既有值，避免 API 用戶端覆寫背景轉錄的狀態。
    /// </summary>
    public async Task<bool> UpdateAsync(Meeting meeting)
    {
        var existing = await context.Meeting.FindAsync(meeting.Id);
        if (existing == null)
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

        context.Entry(existing).CurrentValues.SetValues(meeting);
        await context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// 刪除資料列並回傳剛被刪除的實體，讓呼叫端可以接著移除實體檔案。
    /// </summary>
    public async Task<Meeting?> DeleteAsync(int id)
    {
        var meeting = await context.Meeting.FindAsync(id);
        if (meeting == null)
        {
            return null;
        }

        context.Meeting.Remove(meeting);
        await context.SaveChangesAsync();

        return meeting;
    }

    #endregion
}

using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

public class TeamService
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;

    public IMapper Mapper { get; }
    public ILogger<TeamService> Logger { get; }

    public TeamService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<TeamService> logger,
        ProjectAccessService projectAccess)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.projectAccess = projectAccess;
    }

    public async Task<DataRequestResult<TeamAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading teams. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<TeamAdapterModel> result = new();
        IQueryable<Team> dataSource = context.Team.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Code != null && x.Code.Contains(dataRequest.Search)) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(TeamAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.Code))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Code).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Code).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.IsEnabled))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.UpdatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }
        else
        {
            dataSource = dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<Team> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<TeamAdapterModel>>(records);

        var teamIds = result.Result.Select(x => x.Id).ToList();
        var counts = await context.UserTeam.AsNoTracking()
            .Where(x => teamIds.Contains(x.TeamId))
            .GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count);
        foreach (var item in result.Result)
        {
            item.MemberCount = counts.GetValueOrDefault(item.Id);
        }
        Logger.LogDebug("Loaded teams successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<TeamAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading team by id. TeamId={TeamId}", id);

        Team? item = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Team not found. TeamId={TeamId}", id);
            return new TeamAdapterModel();
        }

        return Mapper.Map<TeamAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(TeamAdapterModel paraObject)
    {
        Logger.LogInformation("Creating team. Name={TeamName}", paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<Team>(context);
            Team itemParameter = Mapper.Map<Team>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Team.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            paraObject.Id = itemParameter.Id;
            CleanTrackingHelper.Clean<Team>(context);

            Logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={TeamName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create team. Name={TeamName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(TeamAdapterModel paraObject)
    {
        Logger.LogInformation("Updating team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<Team>(context);
            Team? item = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Team update rejected because record was not found. TeamId={TeamId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的團隊資料。");
            }

            Team itemData = Mapper.Map<Team>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            CleanTrackingHelper.Clean<Team>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Team>(context);

            Logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={TeamName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting team. TeamId={TeamId}", id);

        try
        {
            CleanTrackingHelper.Clean<Team>(context);
            Team? item = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Team deletion rejected because record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的團隊資料。");
            }

            // 主責必填：還是某個專案的主責時，非管理者不能刪（0.4.102）；管理者不受限（0.4.104）。
            // 畫面有先檢查，這裡再擋一次。
            var inUse = await ProjectTeamWriter.PrimaryInUseMessageAsync(context, id, await projectAccess.GetAsync());
            if (inUse is not null)
            {
                Logger.LogWarning("Team deletion rejected because it is still a primary team. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, inUse);
            }

            CleanTrackingHelper.Clean<Team>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Team>(context);

            Logger.LogInformation("Team deleted successfully. TeamId={TeamId}, Name={TeamName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for team. Name={TeamName}", paraObject.Name);

        var name = (paraObject.Name ?? string.Empty).Trim();
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (nameItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because team name already exists. Name={TeamName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法新增。");
        }

        var code = (paraObject.Code ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(code))
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower());

            if (codeItem != null)
            {
                Logger.LogWarning("Pre-create validation failed because team code already exists. Code={TeamCode}", paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法新增。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        CleanTrackingHelper.Clean<Team>(context);
        var searchItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because team was not found. TeamId={TeamId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的團隊資料不存在。");
        }

        var name = (paraObject.Name ?? string.Empty).Trim();
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (nameItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because team name already exists. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法修改。");
        }

        var code = (paraObject.Code ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(code))
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower() && x.Id != paraObject.Id);

            if (codeItem != null)
            {
                Logger.LogWarning("Pre-update validation failed because team code already exists. TeamId={TeamId}, Code={TeamCode}", paraObject.Id, paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法修改。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeDeleteCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
        // 管理者不受限（0.4.104）：照刪，受影響的專案就沒有主責，之後編輯時再補。
        var inUse = await ProjectTeamWriter.PrimaryInUseMessageAsync(context, paraObject.Id, await projectAccess.GetAsync());
        return inUse is null
            ? VerifyRecordResultFactory.Build(true)
            : VerifyRecordResultFactory.Build(false, inUse);
    }

    /// <summary>
    /// 清單上點狀態膠囊直接切換啟用狀態（0.4.105），不必進編輯視窗。
    /// </summary>
    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool value)
    {
        Logger.LogInformation("Setting Team state. Id={Id}, Value={Value}", id, value);

        try
        {
            CleanTrackingHelper.Clean<Team>(context);

            // 刻意不加 AsNoTracking：這裡要靠變更追蹤把欄位寫回去。
            Team? item = await context.Team.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                Logger.LogWarning("Team state update rejected because record was not found. Id={Id}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要更新的團隊資料。");
            }

            item.IsEnabled = value;
            item.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Team>(context);

            Logger.LogInformation("Team state updated. Id={Id}, Value={Value}", id, value);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set Team state. Id={Id}", id);
            return VerifyRecordResultFactory.Build(false, "更新。", ex);
        }
    }

    public async Task<int?> GetIdByNameAsync(string name)
    {
        return await context.Team.AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>團隊目前的成員（使用者 Id）。</summary>
    public async Task<List<int>> GetMemberIdsAsync(int teamId)
    {
        return await context.UserTeam.AsNoTracking()
            .Where(x => x.TeamId == teamId)
            .Select(x => x.MyUserId)
            .ToListAsync();
    }

    /// <summary>可加進團隊的帳號（啟用中），成員多選用。</summary>
    public async Task<List<MemberOption>> GetSelectableMembersAsync()
    {
        return await context.MyUser.AsNoTracking()
            .Where(x => x.Status)
            .OrderBy(x => x.Name)
            .Select(x => new MemberOption(x.Id, x.Name + "（" + x.Account + "）"))
            .ToListAsync();
    }

    /// <summary>
    /// 把團隊的成員同步成 <paramref name="userIds"/>：多的移除、少的補上。
    /// 專案的主責或協作有這個團隊時，成員就看得到那些專案。
    /// </summary>
    public async Task SyncMembersAsync(int teamId, IEnumerable<int> userIds)
    {
        var wanted = userIds.Distinct().ToList();
        var validIds = await context.MyUser.Where(x => wanted.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        var rows = await context.UserTeam.Where(x => x.TeamId == teamId).ToListAsync();

        context.UserTeam.RemoveRange(rows.Where(x => !validIds.Contains(x.MyUserId)));
        var existing = rows.Select(x => x.MyUserId).ToHashSet();
        context.UserTeam.AddRange(validIds.Where(id => !existing.Contains(id))
            .Select(id => new UserTeam { MyUserId = id, TeamId = teamId }));
        await context.SaveChangesAsync();
        CleanTrackingHelper.Clean<UserTeam>(context);

        Logger.LogInformation("Data group members synced. TeamId={TeamId}, Count={Count}", teamId, validIds.Count);
    }

    public sealed record MemberOption(int Id, string Label);

    /// <summary>
    /// 取得所有啟用中的團隊名稱（依名稱排序），供其他頁面下拉選取使用。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        return await context.Team
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }
}

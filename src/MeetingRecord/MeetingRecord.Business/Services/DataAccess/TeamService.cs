using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

public class TeamService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<TeamService> Logger { get; }

    public TeamService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<TeamService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
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

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

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

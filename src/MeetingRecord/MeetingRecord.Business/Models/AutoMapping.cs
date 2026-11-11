using AutoMapper;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Others;
using static System.Runtime.InteropServices.JavaScript.JSType;
using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Models.Systems;

public class AutoMapping : Profile
{
    public AutoMapping()
    {
        #region Blazor AdapterModel

        #region Project
        // 多值欄位以 TagStringHelper 的換行包夾字串儲存，兩個方向都要轉。
        // ⚠️ 只加這裡不夠：ProjectService.UpdateAsync 是手抄欄位、不走 Mapper。
        // 主責／協作團隊要查詢端 Include(Teams).ThenInclude(Team)，沒 Include 的地方就是空值。
        CreateMap<Project, ProjectAdapterModel>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToList(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToList(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.PrimaryTeamId, o => o.MapFrom(s => s.Teams.Where(t => t.IsPrimary).Select(t => (int?)t.TeamId).FirstOrDefault()))
            .ForMember(d => d.PrimaryTeamName, o => o.MapFrom(s => s.Teams
                .Where(t => t.IsPrimary && t.Team != null)
                .Select(t => t.Team!.Name)
                .FirstOrDefault() ?? string.Empty))
            .ForMember(d => d.CollaboratorTeamIds, o => o.MapFrom(s => s.Teams.Where(t => !t.IsPrimary).Select(t => t.TeamId).ToList()))
            .ForMember(d => d.CollaboratorTeamNames, o => o.MapFrom(s => s.Teams
                .Where(t => !t.IsPrimary && t.Team != null)
                .Select(t => t.Team!.Name)
                .OrderBy(name => name)
                .ToList()));
        CreateMap<ProjectAdapterModel, Project>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToStored(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToStored(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)));
        CreateMap<ProjectFile, ProjectFileAdapterModel>();
        CreateMap<ProjectFileAdapterModel, ProjectFile>();
        #endregion

        #region RoleView
        // 0.4.101 起角色只管功能權限，不再帶預設團隊（DefaultTeamsJson 已刪除）。
        CreateMap<RoleView, RoleViewAdapterModel>();
        CreateMap<RoleViewAdapterModel, RoleView>();
        #endregion

        #region Category
        CreateMap<Category, CategoryAdapterModel>();
        CreateMap<CategoryAdapterModel, Category>();
        #endregion

        #region PromptTemplate
        CreateMap<PromptTemplate, PromptTemplateAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)));
        CreateMap<PromptTemplateAdapterModel, PromptTemplate>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)));
        #endregion

        #region ExceptionLog
        // 系統例外紀錄（0.4.122）：唯讀頁，只需要單向。
        CreateMap<ExceptionLog, ExceptionLogAdapterModel>();
        #endregion

        #region Todo
        CreateMap<Todo, TodoAdapterModel>()
            .ForMember(d => d.ProjectTitle, o => o.MapFrom(s => s.Project != null ? s.Project.Title : null))
            .ForMember(d => d.ProjectStatus, o => o.MapFrom(s => s.Project != null ? s.Project.Status : null))
            .ForMember(d => d.MeetingTitle, o => o.MapFrom(s => s.Meeting != null ? s.Meeting.Title : null))
            // 子項目（1.0.45）：清單由 TodoService 自己組（只分頁主層、套篩選），不讓 AutoMapper 順手帶出整串。
            .ForMember(d => d.ParentTitle, o => o.MapFrom(s => s.Parent != null ? s.Parent.Title : null))
            .ForMember(d => d.Children, o => o.Ignore());
        CreateMap<TodoAdapterModel, Todo>()
            .ForMember(d => d.Project, o => o.Ignore())
            .ForMember(d => d.Meeting, o => o.Ignore())
            // ⚠️ 導覽屬性一律不對應回去：存檔時 EF 會把帶回來的子項目當成新資料整串寫入。
            .ForMember(d => d.Parent, o => o.Ignore())
            .ForMember(d => d.Children, o => o.Ignore());
        // 1.1.18：API 改走 TodoService，對外 DTO 與畫面模型互轉（CanDelete 由 Controller 依呼叫者另算）。
        CreateMap<TodoAdapterModel, TodoDto>()
            .ForMember(d => d.CanDelete, o => o.Ignore());
        CreateMap<TodoCreateUpdateDto, TodoAdapterModel>()
            .ForMember(d => d.Id, o => o.MapFrom(s => s.Id ?? 0))
            .ForMember(d => d.CreatedAt, o => o.Ignore())
            .ForMember(d => d.UpdatedAt, o => o.Ignore())
            .ForMember(d => d.Children, o => o.Ignore());
        #endregion

        #region Meeting
        CreateMap<Meeting, MeetingAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)));
        CreateMap<MeetingAdapterModel, Meeting>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)));
        // 1.1.20：API 改走 MeetingService。分類兩種格式與呼叫者相關的旗標由 Controller 填。
        CreateMap<MeetingAdapterModel, MeetingDto>()
            .ForMember(d => d.Categories, o => o.Ignore())
            .ForMember(d => d.CategoryNames, o => o.Ignore())
            .ForMember(d => d.TranscriptionStatus, o => o.MapFrom(s => (int)s.TranscriptionStatus))
            .ForMember(d => d.DraftStatus, o => o.MapFrom(s => (int)s.DraftStatus))
            .ForMember(d => d.IsUploader, o => o.Ignore())
            .ForMember(d => d.CanDelete, o => o.Ignore())
            // 全文可能很長，只有取單筆時由 Controller 填，清單與搜尋不帶。
            .ForMember(d => d.DraftContent, o => o.Ignore());
        CreateMap<MeetingCreateUpdateDto, MeetingAdapterModel>()
            .ForMember(d => d.Id, o => o.MapFrom(s => s.Id ?? 0))
            .ForMember(d => d.Categories, o => o.Ignore())
            .ForMember(d => d.CreatedAt, o => o.Ignore())
            .ForMember(d => d.UpdatedAt, o => o.Ignore());
        #endregion

        #region Team
        CreateMap<Team, TeamAdapterModel>();
        CreateMap<TeamAdapterModel, Team>();
        #endregion

        #region MyUser
        CreateMap<MyUser, MyUserAdapterModel>();
        CreateMap<MyUserAdapterModel, MyUser>();
        CreateMap<MyUserAdapterModel, CurrentUser>()
            .ForMember(dest => dest.RoleJson, opt => opt.Ignore())
            .ForMember(dest => dest.RoleList, opt => opt.Ignore())
            .ForMember(dest => dest.IsAuthenticated, opt => opt.Ignore());
        #endregion
        #endregion
    }
}

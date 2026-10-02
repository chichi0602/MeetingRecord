using AutoMapper;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Others;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
        CreateMap<Project, ProjectDto>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToList(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToList(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.PrimaryTeamId, o => o.MapFrom(s => s.Teams.Where(t => t.IsPrimary).Select(t => (int?)t.TeamId).FirstOrDefault()))
            .ForMember(d => d.CollaboratorTeamIds, o => o.MapFrom(s => s.Teams.Where(t => !t.IsPrimary).Select(t => t.TeamId).ToList()));
        CreateMap<ProjectDto, Project>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToStored(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToStored(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => s.Categories == null ? null : TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.Ignore());
        // 團隊不在這裡對應：ProjectRepository 依規則自己寫 ProjectTeam（主責必填、非管理者限自己的團隊）。
        CreateMap<Project, ProjectCreateUpdateDto>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToList(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToList(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.PrimaryTeamId, o => o.Ignore())
            .ForMember(d => d.CollaboratorTeamIds, o => o.Ignore());
        CreateMap<ProjectCreateUpdateDto, Project>()
            .ForMember(d => d.GlossaryTerms, o => o.MapFrom(s => TagStringHelper.ToStored(s.GlossaryTerms)))
            .ForMember(d => d.Participants, o => o.MapFrom(s => TagStringHelper.ToStored(s.Participants)))
            .ForMember(d => d.Categories, o => o.MapFrom(s => s.Categories == null ? null : TagStringHelper.ToStored(s.Categories)));
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
        CreateMap<Category, CategoryDto>();
        CreateMap<CategoryDto, Category>();
        CreateMap<Category, CategoryCreateUpdateDto>();
        CreateMap<CategoryCreateUpdateDto, Category>();
        #endregion

        #region PromptTemplate
        CreateMap<PromptTemplate, PromptTemplateAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)));
        CreateMap<PromptTemplateAdapterModel, PromptTemplate>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)));
        CreateMap<PromptTemplate, PromptTemplateDto>();
        CreateMap<PromptTemplateDto, PromptTemplate>();
        CreateMap<PromptTemplate, PromptTemplateCreateUpdateDto>();
        CreateMap<PromptTemplateCreateUpdateDto, PromptTemplate>();
        #endregion

        #region Todo
        CreateMap<Todo, TodoAdapterModel>()
            .ForMember(d => d.ProjectTitle, o => o.MapFrom(s => s.Project != null ? s.Project.Title : null))
            .ForMember(d => d.MeetingTitle, o => o.MapFrom(s => s.Meeting != null ? s.Meeting.Title : null));
        CreateMap<TodoAdapterModel, Todo>()
            .ForMember(d => d.Project, o => o.Ignore())
            .ForMember(d => d.Meeting, o => o.Ignore());
        CreateMap<Todo, TodoDto>()
            .ForMember(d => d.ProjectTitle, o => o.MapFrom(s => s.Project != null ? s.Project.Title : null))
            .ForMember(d => d.MeetingTitle, o => o.MapFrom(s => s.Meeting != null ? s.Meeting.Title : null));
        CreateMap<TodoDto, Todo>()
            .ForMember(d => d.Project, o => o.Ignore())
            .ForMember(d => d.Meeting, o => o.Ignore());
        CreateMap<Todo, TodoCreateUpdateDto>();
        CreateMap<TodoCreateUpdateDto, Todo>()
            .ForMember(d => d.Project, o => o.Ignore())
            .ForMember(d => d.Meeting, o => o.Ignore());
        #endregion

        #region Meeting
        CreateMap<Meeting, MeetingAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)));
        CreateMap<MeetingAdapterModel, Meeting>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)));
        CreateMap<Meeting, MeetingDto>();
        CreateMap<MeetingDto, Meeting>();
        CreateMap<Meeting, MeetingCreateUpdateDto>();
        CreateMap<MeetingCreateUpdateDto, Meeting>();
        #endregion

        #region Team
        CreateMap<Team, TeamAdapterModel>();
        CreateMap<TeamAdapterModel, Team>();
        CreateMap<Team, TeamDto>();
        CreateMap<TeamDto, Team>();
        CreateMap<Team, TeamCreateUpdateDto>();
        CreateMap<TeamCreateUpdateDto, Team>();
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

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
        CreateMap<Project, ProjectAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<ProjectAdapterModel, Project>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Project, ProjectDto>();
        CreateMap<ProjectDto, Project>();
        CreateMap<Project, ProjectCreateUpdateDto>();
        CreateMap<ProjectCreateUpdateDto, Project>();
        CreateMap<ProjectFile, ProjectFileAdapterModel>();
        CreateMap<ProjectFileAdapterModel, ProjectFile>();
        #endregion

        #region RoleView
        CreateMap<RoleView, RoleViewAdapterModel>()
            .ForMember(d => d.DefaultTeams, o => o.MapFrom(s => TeamJsonHelper.Deserialize(s.DefaultTeamsJson)));
        CreateMap<RoleViewAdapterModel, RoleView>()
            .ForMember(d => d.DefaultTeamsJson, o => o.MapFrom(s => TeamJsonHelper.Serialize(s.DefaultTeams)));
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
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<PromptTemplateAdapterModel, PromptTemplate>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<PromptTemplate, PromptTemplateDto>();
        CreateMap<PromptTemplateDto, PromptTemplate>();
        CreateMap<PromptTemplate, PromptTemplateCreateUpdateDto>();
        CreateMap<PromptTemplateCreateUpdateDto, PromptTemplate>();
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

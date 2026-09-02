using System.Text;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

public sealed class MeetingServiceTests
{
    #region 資料往返與標籤字串轉換

    [Fact]
    public async Task AddAsync_ShouldPersistFieldsAndTagStrings()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var model = NewModel("第一次專案會議");
        model.MeetingDate = new DateTime(2026, 8, 21);
        model.Description = "討論里程碑";
        model.Categories = ["專案"];
        model.Teams = ["團隊A"];

        var result = await service.AddAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Title == "第一次專案會議");
        Assert.Equal(new DateTime(2026, 8, 21), saved.MeetingDate);
        Assert.Equal("討論里程碑", saved.Description);
        // 標籤欄位必須經 TagStringHelper 轉為「以換行包夾」的儲存字串，
        // 否則團隊列級權控的 Contains 比對會全面失效。
        Assert.Equal(TagStringHelper.ToStored(["專案"]), saved.Categories);
        Assert.Equal(TagStringHelper.ToStored(["團隊A"]), saved.Teams);
    }

    [Fact]
    public async Task AddAsync_ShouldWriteBackGeneratedId()
    {
        // UI 在新增模式要先拿到 Id 才能把影音檔掛上去，這條回填不能斷。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var model = NewModel("週會");
        await service.AddAsync(model);

        Assert.True(model.Id > 0);
    }

    [Fact]
    public async Task AddAsync_ShouldStartWithNotUploadedStatus()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(NewModel("週會"));

        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.NotUploaded, saved.TranscriptionStatus);
    }

    [Fact]
    public async Task GetAsync_ById_ShouldRoundTripTagsToList()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("季度檢討", categories: ["管理"], teams: ["團隊A", "團隊B"]);
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);

        Assert.Equal("季度檢討", model.Title);
        Assert.Equal(["管理"], model.Categories);
        Assert.Equal(["團隊A", "團隊B"], model.Teams);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceTagsAndKeepCreatedAt()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("季度檢討", teams: ["團隊A"]);
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);
        model.Title = "季度檢討（修訂）";
        model.Teams = ["團隊B"];

        var result = await service.UpdateAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("季度檢討（修訂）", saved.Title);
        Assert.Equal(TagStringHelper.ToStored(["團隊B"]), saved.Teams);
        Assert.Equal(existing.CreatedAt, saved.CreatedAt);
        Assert.True(saved.UpdatedAt >= existing.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotOverwriteMediaAndTranscriptionFields()
    {
        // 畫面上的舊複本不得把背景轉錄剛寫入的狀態蓋掉。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("季度檢討");
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);

        // 模擬背景轉錄在使用者開著 Modal 期間完成
        var tracked = await fixture.Context.Meeting.SingleAsync(x => x.Id == existing.Id);
        tracked.MediaRelativePath = "2026/08/media.mp3";
        tracked.MediaOriginalFileName = "media.mp3";
        tracked.TranscriptRelativePath = "2026/08/transcript.txt";
        tracked.TranscriptionStatus = TranscriptionStatus.Completed;
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        model.Title = "季度檢討（修訂）";
        await service.UpdateAsync(model);

        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("季度檢討（修訂）", saved.Title);
        Assert.Equal("2026/08/media.mp3", saved.MediaRelativePath);
        Assert.Equal("2026/08/transcript.txt", saved.TranscriptRelativePath);
        Assert.Equal(TranscriptionStatus.Completed, saved.TranscriptionStatus);
    }

    #endregion

    #region 刪除連同實體檔案

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRecordAndPhysicalFiles()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var mediaRelativePath = fixture.WriteMediaFile("2026/08/media.mp3", "audio");
        var transcriptRelativePath = fixture.WriteTranscriptFile("2026/08/transcript.txt", "逐字稿內容");

        var existing = await fixture.AddMeetingAsync("要刪除的會議");
        existing.MediaRelativePath = mediaRelativePath;
        existing.TranscriptRelativePath = transcriptRelativePath;
        existing.TranscriptionStatus = TranscriptionStatus.Completed;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.DeleteAsync(existing.Id);

        Assert.True(result.Success);
        Assert.False(await fixture.Context.Meeting.AnyAsync(x => x.Id == existing.Id));
        Assert.False(File.Exists(fixture.MediaFullPath(mediaRelativePath)));
        Assert.False(File.Exists(fixture.TranscriptFullPath(transcriptRelativePath)));
    }

    [Fact]
    public async Task DeleteAsync_ShouldSucceed_WhenPhysicalFilesAreAlreadyGone()
    {
        // 實體檔案被外部移走時，資料列仍必須刪得掉。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("檔案已遺失的會議");
        existing.MediaRelativePath = "2026/08/missing.mp3";
        existing.TranscriptRelativePath = "2026/08/missing.txt";
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.DeleteAsync(existing.Id);

        Assert.True(result.Success);
        Assert.False(await fixture.Context.Meeting.AnyAsync(x => x.Id == existing.Id));
    }

    #endregion

    #region 影音檔上傳

    [Fact]
    public async Task SaveMediaAsync_ShouldStoreFileMarkPendingAndEnqueue()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("要上傳的會議");
        var service = fixture.CreateService();

        var payload = Encoding.UTF8.GetBytes("fake-audio-bytes");
        var reported = new List<int>();
        var result = await service.SaveMediaAsync(
            existing.Id,
            NewUpload("錄音.mp3", payload),
            new CollectingProgress(reported));

        Assert.True(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("錄音.mp3", saved.MediaOriginalFileName);
        Assert.Equal(payload.Length, saved.MediaFileSize);
        Assert.EndsWith(".mp3", saved.MediaStoredFileName);
        Assert.Equal(TranscriptionStatus.Pending, saved.TranscriptionStatus);
        Assert.True(File.Exists(fixture.MediaFullPath(saved.MediaRelativePath!)));
        Assert.Equal(payload, await File.ReadAllBytesAsync(fixture.MediaFullPath(saved.MediaRelativePath!)));

        Assert.Equal([existing.Id], fixture.Queue.Enqueued);
        Assert.Contains(100, reported);
    }

    [Fact]
    public async Task SaveMediaAsync_ShouldFillMeetingDate_WhenNotProvided()
    {
        // 使用者常常只丟檔案不填日期，此時以上傳當天為準，之後可再手動更正。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("沒填日期的會議");
        Assert.Null(existing.MeetingDate);
        var service = fixture.CreateService();

        var result = await service.SaveMediaAsync(existing.Id, NewUpload("錄音.mp3", [1, 2, 3]));

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(DateTime.Today, saved.MeetingDate);
    }

    [Fact]
    public async Task SaveMediaAsync_ShouldKeepMeetingDate_WhenAlreadySet()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("已填日期的會議");
        var originalDate = new DateTime(2026, 8, 20);
        existing.MeetingDate = originalDate;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();

        var result = await service.SaveMediaAsync(existing.Id, NewUpload("錄音.mp3", [1, 2, 3]));

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(originalDate, saved.MeetingDate);
    }

    [Fact]
    public async Task SaveMediaAsync_ShouldRejectUnsupportedExtension()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("要上傳的會議");
        var service = fixture.CreateService();

        var result = await service.SaveMediaAsync(existing.Id, NewUpload("紀錄.txt", [1, 2, 3]));

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task SaveMediaAsync_ShouldRejectOversizedFile()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("要上傳的會議");
        var service = fixture.CreateService();

        var upload = NewUpload("錄音.mp3", [1, 2, 3]);
        upload.FileSize = MeetingMediaPolicy.MaxUploadFileSize + 1;

        var result = await service.SaveMediaAsync(existing.Id, upload);

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task SaveMediaAsync_ShouldReplacePreviousMediaAndTranscript()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var oldMedia = fixture.WriteMediaFile("2026/08/old.mp3", "old-audio");
        var oldTranscript = fixture.WriteTranscriptFile("2026/08/old.txt", "舊逐字稿");

        var existing = await fixture.AddMeetingAsync("要換檔的會議");
        existing.MediaRelativePath = oldMedia;
        existing.MediaOriginalFileName = "old.mp3";
        existing.TranscriptRelativePath = oldTranscript;
        existing.TranscriptionStatus = TranscriptionStatus.Completed;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.SaveMediaAsync(existing.Id, NewUpload("新錄音.wav", Encoding.UTF8.GetBytes("new-audio")));

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("新錄音.wav", saved.MediaOriginalFileName);
        Assert.Null(saved.TranscriptRelativePath);
        Assert.Equal(TranscriptionStatus.Pending, saved.TranscriptionStatus);
        Assert.False(File.Exists(fixture.MediaFullPath(oldMedia)));
        Assert.False(File.Exists(fixture.TranscriptFullPath(oldTranscript)));
    }

    [Fact]
    public async Task SaveMediaAsync_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("團隊B的會議", teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.SaveMediaAsync(existing.Id, NewUpload("錄音.mp3", [1, 2, 3]));

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    #endregion

    #region 重新轉錄與逐字稿預覽

    [Fact]
    public async Task RequeueTranscriptionAsync_ShouldResetStatusAndEnqueue()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("失敗的會議");
        existing.MediaRelativePath = "2026/08/media.mp3";
        existing.TranscriptionStatus = TranscriptionStatus.Failed;
        existing.TranscriptionError = "API 金鑰錯誤";
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.RequeueTranscriptionAsync(existing.Id);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(TranscriptionStatus.Pending, saved.TranscriptionStatus);
        Assert.Null(saved.TranscriptionError);
        Assert.Equal([existing.Id], fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task RequeueTranscriptionAsync_ShouldFail_WhenNoMediaUploaded()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("沒有影音檔的會議");
        var service = fixture.CreateService();

        var result = await service.RequeueTranscriptionAsync(existing.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task RequeueTranscriptionAsync_ShouldFail_WhenAlreadyProcessing()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("轉錄中的會議");
        existing.MediaRelativePath = "2026/08/media.mp3";
        existing.TranscriptionStatus = TranscriptionStatus.Processing;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.RequeueTranscriptionAsync(existing.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task ReadTranscriptAsync_ShouldReturnFileContent()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var transcriptRelativePath = fixture.WriteTranscriptFile("2026/08/transcript.txt", "會議逐字稿內容");

        var existing = await fixture.AddMeetingAsync("已完成的會議");
        existing.TranscriptRelativePath = transcriptRelativePath;
        existing.TranscriptionStatus = TranscriptionStatus.Completed;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var content = await service.ReadTranscriptAsync(existing.Id);

        Assert.Equal("會議逐字稿內容", content);
    }

    [Fact]
    public async Task ReadTranscriptAsync_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var transcriptRelativePath = fixture.WriteTranscriptFile("2026/08/transcript.txt", "機密逐字稿");

        var existing = await fixture.AddMeetingAsync("團隊B的會議", teams: ["團隊B"]);
        existing.TranscriptRelativePath = transcriptRelativePath;
        existing.TranscriptionStatus = TranscriptionStatus.Completed;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService(isAdmin: false, "團隊A");
        var content = await service.ReadTranscriptAsync(existing.Id);

        Assert.Null(content);
    }

    #endregion

    #region AI 會議紀錄草稿

    [Fact]
    public async Task RequestDraftAsync_ShouldEnqueueAndAssignProject_WhenTranscriptIsReady()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.True(result.Success);
        Assert.Equal(meeting.Id, Assert.Single(fixture.DraftQueue.Enqueued));

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal(DraftStatus.Pending, saved.DraftStatus);
        Assert.Equal(template.Id, saved.DraftPromptTemplateId);
        // 提示詞名稱以快照保存，日後範本改名或刪除仍看得出當初用了什麼。
        Assert.Equal("標準會議紀錄", saved.DraftPromptTemplateName);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenTranscriptIsNotCompleted()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddMeetingAsync("尚未轉錄的會議");
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenTranscriptBelongsToAnotherProject()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var owner = await fixture.AddProjectAsync("客戶訪談專案");
        var other = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: owner.Id);
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, other.Id, template.Id);

        Assert.False(result.Success);
        Assert.Contains("已歸屬於其他專案", result.Message);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldAllowRegeneration_WhenTranscriptBelongsToSameProject()
    {
        // 換提示詞重新生成是預期用法，會覆蓋既有草稿。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("決議導向摘要");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: project.Id);
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.True(result.Success);
        Assert.Single(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenDraftIsAlreadyProcessing()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "需求確認會議",
            draftStatus: DraftStatus.Processing);
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenMeetingIsOutOfTeamScope()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("團隊B會議", teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task GetSelectableTranscriptsAsync_ShouldReturnCompletedOnlyWithAssignment()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        await fixture.AddMeetingAsync("尚未轉錄的會議");
        await fixture.AddCompletedMeetingAsync("未歸屬逐字稿");
        await fixture.AddCompletedMeetingAsync("已歸屬逐字稿", projectId: project.Id);
        var service = fixture.CreateService();

        var result = await service.GetSelectableTranscriptsAsync();

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(x => x.Title == "未歸屬逐字稿").IsUnassigned);
        Assert.Equal("客戶訪談專案", result.Single(x => x.Title == "已歸屬逐字稿").ProjectTitle);
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotOverwriteDraftWrittenByBackgroundJob()
    {
        // 畫面上的舊複本存檔時，不得清掉背景生成寫入的草稿。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        meeting.DraftContent = "AI 產生的會議紀錄";
        meeting.DraftStatus = DraftStatus.Completed;
        fixture.Context.Meeting.Update(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var stale = await service.GetAsync(meeting.Id);
        stale.Title = "改過標題的舊複本";
        stale.DraftContent = null;
        stale.DraftStatus = DraftStatus.NotGenerated;

        var result = await service.UpdateAsync(stale);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal("改過標題的舊複本", saved.Title);
        Assert.Equal("AI 產生的會議紀錄", saved.DraftContent);
        Assert.Equal(DraftStatus.Completed, saved.DraftStatus);
    }

    #endregion

    #region 團隊可見性

    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result.Result, x => x.Title == "團隊B會議");
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(1, result.Count);
        Assert.Equal("公開會議", Assert.Single(result.Result).Title);
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var model = await service.GetAsync(ids["團隊B會議"]);

        Assert.Equal(0, model.Id);
    }

    [Fact]
    public async Task GetAsync_WithTeamFilter_ShouldFilterByTeam()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService();

        var request = NewRequest();
        request.TeamFilters = ["團隊B"];
        var result = await service.GetAsync(request);

        Assert.Equal("團隊B會議", Assert.Single(result.Result).Title);
    }

    [Fact]
    public async Task GetAsync_WithKeyword_ShouldMatchMediaFileName()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("週會");
        existing.MediaOriginalFileName = "20260821-週會錄音.mp3";
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await fixture.AddMeetingAsync("月會");
        var service = fixture.CreateService();

        var request = NewRequest();
        request.Search = "週會錄音";
        var result = await service.GetAsync(request);

        Assert.Equal("週會", Assert.Single(result.Result).Title);
    }

    #endregion

    #region 測試輔助

    private static MeetingAdapterModel NewModel(string title) => new()
    {
        Title = title,
    };

    private static MeetingMediaUploadInput NewUpload(string fileName, byte[] payload) => new()
    {
        FileName = fileName,
        ContentType = "application/octet-stream",
        FileSize = payload.Length,
        Content = new MemoryStream(payload),
    };

    private static DataRequest NewRequest() => new()
    {
        Search = string.Empty,
        SortField = string.Empty,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class FakeScopeProvider(bool isAdmin, IReadOnlyList<string> teams) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }

    private sealed class FakeTranscriptionQueue : ITranscriptionQueue
    {
        public List<int> Enqueued { get; } = [];

        public ValueTask EnqueueAsync(int meetingId, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(meetingId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> DequeueAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("測試不會消費佇列。");
    }

    private sealed class FakeMeetingDraftQueue : IMeetingDraftQueue
    {
        public List<int> Enqueued { get; } = [];

        public ValueTask EnqueueAsync(int meetingId, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(meetingId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> DequeueAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("測試不會消費佇列。");
    }

    private sealed class CollectingProgress(List<int> reported) : IProgress<int>
    {
        public void Report(int value) => reported.Add(value);
    }

    private sealed class MeetingServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;
        private readonly string rootPath;
        private readonly MeetingFileStore fileStore;

        private MeetingServiceFixture(SqliteConnection connection, BackendDBContext context, string rootPath)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();

            var systemSettings = new SystemSettings();
            systemSettings.ExternalFileSystem.MeetingMediaPath = MediaRoot;
            systemSettings.ExternalFileSystem.MeetingTranscriptPath = TranscriptRoot;

            fileStore = new MeetingFileStore(
                Options.Create(systemSettings),
                loggerFactory.CreateLogger<MeetingFileStore>());
        }

        public BackendDBContext Context { get; }

        public FakeTranscriptionQueue Queue { get; } = new();

        public FakeMeetingDraftQueue DraftQueue { get; } = new();

        public MeetingDraftProgressNotifier DraftProgressNotifier { get; } = new();

        /// <summary>進度通知器沒有外部相依，直接用真的，順便驗證服務層有把工作登錄進去。</summary>
        public TranscriptionProgressNotifier ProgressNotifier { get; } = new();

        public string MediaRoot => Path.Combine(rootPath, "media");

        public string TranscriptRoot => Path.Combine(rootPath, "transcript");

        public static async Task<MeetingServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            return new MeetingServiceFixture(connection, context, rootPath);
        }

        public MeetingService CreateService(bool isAdmin = true, params string[] teams)
        {
            return new MeetingService(
                Context,
                mapper,
                loggerFactory.CreateLogger<MeetingService>(),
                new FakeScopeProvider(isAdmin, teams),
                fileStore,
                Queue,
                ProgressNotifier,
                DraftQueue,
                DraftProgressNotifier);
        }

        public async Task<Meeting> AddMeetingAsync(
            string title,
            IEnumerable<string>? categories = null,
            IEnumerable<string>? teams = null)
        {
            var meeting = new Meeting
            {
                Title = title,
                Categories = TagStringHelper.ToStored(categories),
                Teams = TagStringHelper.ToStored(teams),
            };

            Context.Meeting.Add(meeting);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return meeting;
        }

        /// <summary>建立一筆轉錄已完成、可供產生草稿的會議。</summary>
        public async Task<Meeting> AddCompletedMeetingAsync(
            string title,
            int? projectId = null,
            DraftStatus draftStatus = DraftStatus.NotGenerated,
            IEnumerable<string>? teams = null)
        {
            var meeting = new Meeting
            {
                Title = title,
                TranscriptionStatus = TranscriptionStatus.Completed,
                TranscriptRelativePath = $"2026/08/{Guid.NewGuid():N}.txt",
                ProjectId = projectId,
                DraftStatus = draftStatus,
                Teams = TagStringHelper.ToStored(teams),
            };

            Context.Meeting.Add(meeting);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return meeting;
        }

        public async Task<Project> AddProjectAsync(string title)
        {
            var project = new Project
            {
                Title = title,
                Status = "進行中",
                Owner = "王小明",
            };

            Context.Project.Add(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return project;
        }

        public async Task<PromptTemplate> AddPromptTemplateAsync(string name, IEnumerable<string>? teams = null)
        {
            var template = new PromptTemplate
            {
                Name = name,
                Content = "請根據以下逐字稿整理會議紀錄：{{transcript}}",
                IsEnabled = true,
                Teams = TagStringHelper.ToStored(teams),
            };

            Context.PromptTemplate.Add(template);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return template;
        }

        public async Task<Dictionary<string, int>> SeedDefaultMeetingsAsync()
        {
            var pub = await AddMeetingAsync("公開會議");
            var teamA = await AddMeetingAsync("團隊A會議", teams: ["團隊A"]);
            var teamB = await AddMeetingAsync("團隊B會議", teams: ["團隊B"]);

            return new Dictionary<string, int>
            {
                ["公開會議"] = pub.Id,
                ["團隊A會議"] = teamA.Id,
                ["團隊B會議"] = teamB.Id,
            };
        }

        public string MediaFullPath(string relativePath) => fileStore.GetMediaFullPath(relativePath);

        public string TranscriptFullPath(string relativePath) => fileStore.GetTranscriptFullPath(relativePath);

        public string WriteMediaFile(string relativePath, string content)
        {
            WriteFile(MediaFullPath(relativePath), content);
            return relativePath;
        }

        public string WriteTranscriptFile(string relativePath, string content)
        {
            WriteFile(TranscriptFullPath(relativePath), content);
            return relativePath;
        }

        private static void WriteFile(string fullPath, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    #endregion
}

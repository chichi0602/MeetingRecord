using System.Text;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Others;
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
        model.Categories = ["專案", "客戶"];

        var result = await service.AddAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Title == "第一次專案會議");
        Assert.Equal(new DateTime(2026, 8, 21), saved.MeetingDate);
        Assert.Equal("討論里程碑", saved.Description);
        // 標籤欄位必須經 TagStringHelper 轉為「以換行包夾」的儲存字串，
        // 否則分類篩選的 Contains 比對會全面失效。
        Assert.Equal(TagStringHelper.ToStored(["專案", "客戶"]), saved.Categories);
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
        var existing = await fixture.AddMeetingAsync("季度檢討", categories: ["管理", "週報"]);
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);

        Assert.Equal("季度檢討", model.Title);
        Assert.Equal(["管理", "週報"], model.Categories);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceTagsAndKeepCreatedAt()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("季度檢討", categories: ["管理"]);
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);
        model.Title = "季度檢討（修訂）";
        model.Categories = ["週報"];

        var result = await service.UpdateAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("季度檢討（修訂）", saved.Title);
        Assert.Equal(TagStringHelper.ToStored(["週報"]), saved.Categories);
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

    [Theory]
    [InlineData(TranscriptionStatus.Pending, DraftStatus.NotGenerated)]
    [InlineData(TranscriptionStatus.Processing, DraftStatus.NotGenerated)]
    [InlineData(TranscriptionStatus.Completed, DraftStatus.Pending)]
    [InlineData(TranscriptionStatus.Completed, DraftStatus.Processing)]
    public async Task DeleteAsync_ShouldReject_WhileBackgroundJobIsActive(TranscriptionStatus transcription, DraftStatus draft)
    {
        // 0.4.115：工作不會因為刪除而停下，跑完寫出的逐字稿檔沒有資料列可掛，會永遠留在磁碟上。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("跑到一半的會議");
        existing.TranscriptionStatus = transcription;
        existing.DraftStatus = draft;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await fixture.CreateService().DeleteAsync(existing.Id);

        Assert.False(result.Success);
        Assert.True(await fixture.Context.Meeting.AnyAsync(x => x.Id == existing.Id));
    }

    #endregion

    #region 影音檔上傳

    [Theory]
    [InlineData(TranscriptionStatus.Pending, DraftStatus.NotGenerated)]
    [InlineData(TranscriptionStatus.Processing, DraftStatus.NotGenerated)]
    [InlineData(TranscriptionStatus.Completed, DraftStatus.Processing)]
    public async Task SaveMediaAsync_ShouldReject_WhileBackgroundJobIsActive(TranscriptionStatus transcription, DraftStatus draft)
    {
        // 0.4.115：舊檔那趟轉錄不會停，跑完會把舊錄音的逐字稿寫到已經換成新檔的會議上。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("轉錄中的會議");
        existing.TranscriptionStatus = transcription;
        existing.DraftStatus = draft;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await fixture.CreateService().SaveMediaAsync(
            existing.Id, NewUpload("新錄音.mp3", Encoding.UTF8.GetBytes("new-audio")));

        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
        Assert.False(Directory.Exists(fixture.MediaRoot) && Directory.EnumerateFiles(fixture.MediaRoot, "*", SearchOption.AllDirectories).Any());
    }

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
    public async Task SaveMediaAsync_NonMember_ShouldDenyMeetingOfOtherProject()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var (userA, _, ids) = await fixture.SeedDefaultMeetingsAsync();
        var existing = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == ids["B專案會議"]);
        var service = fixture.CreateService(isAdmin: false, userA);

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
    public async Task RequeueTranscriptionAsync_ShouldFail_WhenAlreadyPending()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("已排入轉錄的會議");
        existing.MediaRelativePath = "2026/08/media.mp3";
        existing.TranscriptionStatus = TranscriptionStatus.Pending;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.RequeueTranscriptionAsync(existing.Id);

        // 只擋 Processing 不夠：Pending 代表已入列但還沒開工，放行就會入列第二次。
        Assert.False(result.Success);
        Assert.Empty(fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task RequeueTranscriptionAsync_ShouldEnqueueOnce_WhenRequestedTwiceInARow()
    {
        // 這是重複計費缺陷的迴歸測試。單測 Pending 狀態不足以證明這個序列——
        // 缺陷的形狀是「第一次成功後狀態變 Pending，第二次仍被放行」，
        // 兩筆入列會被單一 worker 依序跑兩趟完整轉錄，帳單收兩份。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("被連按兩下的會議");
        existing.MediaRelativePath = "2026/08/media.mp3";
        existing.TranscriptionStatus = TranscriptionStatus.Failed;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var first = await service.RequeueTranscriptionAsync(existing.Id);
        var second = await service.RequeueTranscriptionAsync(existing.Id);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal([existing.Id], fixture.Queue.Enqueued);
    }

    [Fact]
    public async Task RequeueTranscriptionAsync_ShouldResetStatusAndEnqueue_WhenPreviousRunWasCancelled()
    {
        // 取消之後必須能重跑：取消不保留進度，只能整個重來。
        // 0.4.65 之前 CanRetryTranscription 是列舉式且漏了 Cancelled，
        // 服務層允許但畫面不給按，取消等於死路。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var existing = await fixture.AddMeetingAsync("被取消的會議");
        existing.MediaRelativePath = "2026/08/media.mp3";
        existing.TranscriptionStatus = TranscriptionStatus.Cancelled;
        fixture.Context.Meeting.Update(existing);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.RequeueTranscriptionAsync(existing.Id);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(TranscriptionStatus.Pending, saved.TranscriptionStatus);
        Assert.Equal([existing.Id], fixture.Queue.Enqueued);
    }
    #endregion

    #region AI 會議紀錄草稿

    [Fact]
    public async Task DetachFromProjectAsync_ShouldClearOwnershipAndDraft_ButKeepFiles()
    {
        // 「生成錯專案」的出口：解除歸屬並清掉草稿，但影音檔與逐字稿一定要留著——
        // 整筆刪掉的話使用者得重新上傳並重新付一次轉錄費用。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("走錯的專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, project.Id, template.Id, ["王小明"]);

        var queued = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        queued.DraftStatus = DraftStatus.Completed;
        queued.DraftContent = "產生好的會議紀錄";
        fixture.Context.Meeting.Update(queued);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await service.DetachFromProjectAsync(meeting.Id, project.Id);

        Assert.True(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
        Assert.Null(saved.DraftContent);
        Assert.Equal(DraftStatus.NotGenerated, saved.DraftStatus);
        Assert.Null(saved.DraftPromptTemplateId);
        Assert.Null(saved.DraftPromptTemplateName);
        Assert.Null(saved.DraftAttendees);

        // 這兩個是重點：檔案路徑還在，逐字稿與影音沒有被動到。
        Assert.Equal(TranscriptionStatus.Completed, saved.TranscriptionStatus);
        Assert.False(string.IsNullOrWhiteSpace(saved.TranscriptRelativePath));
    }

    [Fact]
    public async Task DetachFromProjectAsync_ThenRequestDraft_ShouldAllowAnotherProject()
    {
        // 移除的目的就是這個：讓同一份逐字稿可以改指到正確的專案。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var wrong = await fixture.AddProjectAsync("走錯的專案");
        var right = await fixture.AddProjectAsync("正確的專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, wrong.Id, template.Id);

        // 未移除前，指到別的專案會被擋。
        var blocked = await service.RequestDraftAsync(meeting.Id, right.Id, template.Id);
        Assert.False(blocked.Success);

        await MarkDraftCompletedAsync(fixture, meeting.Id);
        Assert.True((await service.DetachFromProjectAsync(meeting.Id, wrong.Id)).Success);

        var retry = await service.RequestDraftAsync(meeting.Id, right.Id, template.Id);

        Assert.True(retry.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(right.Id, saved.ProjectId);
    }

    [Fact]
    public async Task DetachFromProjectAsync_ShouldRejectWhenBelongsToAnotherProject()
    {
        // 畫面過期：這筆其實已經被移到別的專案了。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var projectA = await fixture.AddProjectAsync("專案A");
        var projectB = await fixture.AddProjectAsync("專案B");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, projectA.Id, template.Id);

        var result = await service.DetachFromProjectAsync(meeting.Id, projectB.Id);

        Assert.False(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(projectA.Id, saved.ProjectId);
    }

    [Theory]
    [InlineData(DraftStatus.Pending)]
    [InlineData(DraftStatus.Processing)]
    public async Task DetachFromProjectAsync_ShouldRejectWhileGenerating(DraftStatus status)
    {
        // job runner 只吃 meetingId，結束時會把草稿寫回這筆紀錄——
        // 生成途中解除歸屬會變成「已移除卻又冒出一份草稿」。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        var queued = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        queued.DraftStatus = status;
        fixture.Context.Meeting.Update(queued);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await service.DetachFromProjectAsync(meeting.Id, project.Id);

        Assert.False(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(project.Id, saved.ProjectId);
    }

    [Fact]
    public async Task DetachFromProjectAsync_ShouldFailWhenMeetingIsMissing()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        Assert.False((await service.DetachFromProjectAsync(999999, 1)).Success);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldEnqueueWithoutProject_WhenProjectIdIsNull()
    {
        // 真值表 (a)：不必先建專案也能產生會議紀錄，這是整個功能的前提。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("臨時討論");
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, null, template.Id);

        Assert.True(result.Success);
        Assert.Equal(meeting.Id, Assert.Single(fixture.DraftQueue.Enqueued));

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
        Assert.Equal(DraftStatus.Pending, saved.DraftStatus);
        Assert.Equal(template.Id, saved.DraftPromptTemplateId);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldKeepExistingProject_WhenProjectIdIsNull()
    {
        // 真值表 (c)：null 的語意是「不指定」，不是「解除歸屬」。
        // 這支釘死一個會編譯成功但語意相反的寫法——少了 projectId is not null 這個條件，
        // 已歸屬的會議會因為 A != null 被判成「已歸屬其他專案」而拒絕。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: project.Id);
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, null, template.Id);

        Assert.True(result.Success);
        Assert.Single(fixture.DraftQueue.Enqueued);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(project.Id, saved.ProjectId);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenProjectDoesNotExist()
    {
        // 防止有人為了讓 null 過關，把整段專案存在性檢查包進 null 判斷而誤放行不存在的 Id。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(meeting.Id, 999999, template.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldStoreAttendees_WhenNoProject()
    {
        // 沒有專案名冊也可以手動帶與會者進來，快照照存。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("臨時討論");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, null, template.Id, ["王小明"]);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
        Assert.Equal("\n王小明\n", saved.DraftAttendees);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldSetProjectId_AndKeepDraftIntact()
    {
        // 這支的核心承諾：只補歸屬，不重跑、不花錢。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var meeting = await fixture.AddCompletedMeetingAsync("臨時討論", draftStatus: DraftStatus.Completed);
        meeting.DraftContent = "AI 產生的會議紀錄";
        meeting.DraftPromptTemplateId = 7;
        meeting.DraftPromptTemplateName = "標準會議紀錄";
        meeting.DraftAttendees = "\n王小明\n";
        meeting.DraftCompletedAt = new DateTime(2026, 9, 15, 10, 0, 0);
        fixture.Context.Meeting.Update(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var result = await service.AttachToProjectAsync(meeting.Id, project.Id);

        Assert.True(result.Success);
        // 不重新入列是整個功能的存在理由——重跑要再付一次 API 費用。
        Assert.Empty(fixture.DraftQueue.Enqueued);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal("AI 產生的會議紀錄", saved.DraftContent);
        Assert.Equal(DraftStatus.Completed, saved.DraftStatus);
        Assert.Equal(7, saved.DraftPromptTemplateId);
        Assert.Equal("標準會議紀錄", saved.DraftPromptTemplateName);
        Assert.Equal("\n王小明\n", saved.DraftAttendees);
        Assert.Equal(new DateTime(2026, 9, 15, 10, 0, 0), saved.DraftCompletedAt);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldReject_WhenAlreadyInSameProject()
    {
        // 畫面過期，重新整理就好——訊息要和「屬於別的專案」分開。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: project.Id);
        var service = fixture.CreateService();

        var result = await service.AttachToProjectAsync(meeting.Id, project.Id);

        Assert.False(result.Success);
        Assert.Contains("已經屬於這個專案", result.Message);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldReject_WhenBelongsToAnotherProject()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var owner = await fixture.AddProjectAsync("客戶訪談專案");
        var other = await fixture.AddProjectAsync("Q3 產品改版專案");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: owner.Id);
        var service = fixture.CreateService();

        var result = await service.AttachToProjectAsync(meeting.Id, other.Id);

        Assert.False(result.Success);
        // 得先去原專案移除，訊息要講得出下一步。
        Assert.Contains("請先從該專案移除", result.Message);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(owner.Id, saved.ProjectId);
    }

    [Theory]
    [InlineData(DraftStatus.Pending)]
    [InlineData(DraftStatus.Processing)]
    public async Task AttachToProjectAsync_ShouldRejectWhileGenerating(DraftStatus draftStatus)
    {
        // 生成開始時就用當下的 ProjectId 決定要套哪一份常用名詞；
        // 中途歸屬過去的草稿其實沒套到，卻會躺在那個專案的清單裡。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var meeting = await fixture.AddCompletedMeetingAsync("生成中的會議", draftStatus: draftStatus);
        var service = fixture.CreateService();

        var result = await service.AttachToProjectAsync(meeting.Id, project.Id);

        Assert.False(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldReject_WhenProjectIsMissing()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var meeting = await fixture.AddCompletedMeetingAsync("臨時討論");
        var service = fixture.CreateService();

        var result = await service.AttachToProjectAsync(meeting.Id, 999999);

        Assert.False(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldReject_WhenTargetProjectIsNotVisible()
    {
        // 會議是自己上傳的（看得到），但目標專案的團隊自己不在：不能把會議塞進別人的專案。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        await fixture.AddMemberAsync(project.Id, bob.Id);
        var meeting = await fixture.AddCompletedMeetingAsync("自己的會議", createdByUserId: user.Id);
        var service = fixture.CreateService(isAdmin: false, user.Id);

        var result = await service.AttachToProjectAsync(meeting.Id, project.Id);

        Assert.False(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
    }

    [Fact]
    public async Task AttachToProjectAsync_ShouldFailWhenMeetingIsMissing()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        Assert.False((await service.AttachToProjectAsync(999999, 1)).Success);
    }

    [Fact]
    public async Task AttachToProjectAsync_ThenDetach_ShouldClearDraft()
    {
        // 釘死一個反直覺但刻意的行為：兩支方法不是可逆的一對。
        // 補歸屬只寫 ProjectId，移除卻會連草稿一起刪——確認文案必須講清楚這件事。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var meeting = await fixture.AddCompletedMeetingAsync("臨時討論", draftStatus: DraftStatus.Completed);
        meeting.DraftContent = "AI 產生的會議紀錄";
        fixture.Context.Meeting.Update(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        Assert.True((await service.AttachToProjectAsync(meeting.Id, project.Id)).Success);
        Assert.True((await service.DetachFromProjectAsync(meeting.Id, project.Id)).Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.ProjectId);
        Assert.Null(saved.DraftContent);
        Assert.Equal(DraftStatus.NotGenerated, saved.DraftStatus);
        // 影音檔與逐字稿仍然保留。
        Assert.NotNull(saved.TranscriptRelativePath);
    }

    private static async Task MarkDraftCompletedAsync(MeetingServiceFixture fixture, int meetingId)
    {
        // 被擋下來的 RequestDraftAsync 會提早 return、沒有清追蹤，直接 Update 會撞 identity conflict。
        fixture.Context.ChangeTracker.Clear();

        var meeting = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meetingId);
        meeting.DraftStatus = DraftStatus.Completed;
        fixture.Context.Meeting.Update(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldStoreAttendeesSnapshot()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        var result = await service.RequestDraftAsync(
            meeting.Id, project.Id, template.Id, ["王小明", " 陳大文 ", "  ", "王小明"]);

        Assert.True(result.Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        // ToStored 負責 trim、捨棄空項、忽略大小寫去重並保留原順序。
        Assert.Equal("\n王小明\n陳大文\n", saved.DraftAttendees);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldLeaveAttendeesNull_WhenNoneSelected()
    {
        // 與會者是選填。沒勾選時欄位要是 null，讓 JobRunner 組出空字串、提示詞完全不變。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議");
        var service = fixture.CreateService();

        await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Null(saved.DraftAttendees);
    }

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
    public async Task RequestDraftAsync_ShouldCarryTheRequesterIntoTheQueue()
    {
        // ⚠️ 用量帳本要回答「誰在燒錢」，而生成跑在背景服務自建的 scope 裡——
        // 那裡的 CurrentUser 是空白物件（Id=0、Name=""），不是 null 也不會拋例外。
        // 所以觸發者一定要在入列這一刻就抓下來帶著走；漏掉的話，所有背景呼叫
        // 都會被默默記成無名氏，而且完全不報錯。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("已轉錄的會議");

        fixture.CurrentUserService.CurrentUser = new CurrentUser { Id = 7, Name = "王小明", Account = "wang" };

        var result = await fixture.CreateService().RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.True(result.Success);

        var request = Assert.Single(fixture.DraftQueue.Requests);
        Assert.Equal(meeting.Id, request.MeetingId);
        Assert.Equal(7, request.RequestedByUserId);
        Assert.Equal("王小明", request.RequestedByUserName);
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
    public async Task RequestDraftAsync_ShouldReject_WhenMeetingIsUploadedBySomeoneElse()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        await fixture.AddMemberAsync(project.Id, alice.Id);
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("bob 還沒歸屬的會議", createdByUserId: bob.Id);
        var service = fixture.CreateService(isAdmin: false, alice.Id);

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
    public async Task GetAsync_ShouldFillProjectTitle_ForClipboardList()
    {
        // 清單查詢少了 Include(Project) 的話，ProjectTitle 永遠是 null、
        // 整欄會顯示「— 未歸屬」，而 ProjectId 卻是對的——只有顯示壞掉，很難察覺。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        await fixture.AddCompletedMeetingAsync("已歸屬會議", projectId: project.Id);
        await fixture.AddCompletedMeetingAsync("未歸屬會議");
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest());

        var assigned = result.Result!.Single(x => x.Title == "已歸屬會議");
        Assert.Equal("客戶訪談專案", assigned.ProjectTitle);
        Assert.False(assigned.IsUnassigned);

        var unassigned = result.Result!.Single(x => x.Title == "未歸屬會議");
        Assert.True(unassigned.IsUnassigned);
        Assert.Equal("— 未歸屬", unassigned.ProjectTitleText);
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotOverwriteAttendeesAndOwnership()
    {
        // AdapterModel 上根本沒有 DraftAttendees，Mapper 一定產出 null；
        // 不從資料庫沿用的話，光是在畫面上改個標題就會把與會者快照清掉。
        // ProjectId 同理：歸屬的權威路徑是 Attach/Detach，不是這張表單。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("客戶訪談專案");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: project.Id);
        meeting.DraftAttendees = "\n王小明\n陳大文\n";
        fixture.Context.Meeting.Update(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var service = fixture.CreateService();
        var stale = await service.GetAsync(meeting.Id);
        stale.Title = "改過標題的舊複本";
        stale.ProjectId = null;

        Assert.True((await service.UpdateAsync(stale)).Success);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal("改過標題的舊複本", saved.Title);
        Assert.Equal("\n王小明\n陳大文\n", saved.DraftAttendees);
        Assert.Equal(project.Id, saved.ProjectId);
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


    [Fact]
    public async Task RequestDraftAsync_ShouldReject_WhenDraftIsAlreadyPending()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "需求確認會議",
            projectId: project.Id,
            draftStatus: DraftStatus.Pending);

        var service = fixture.CreateService();
        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.False(result.Success);
        Assert.Empty(fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldEnqueueOnce_WhenRequestedTwiceInARow()
    {
        // 同轉錄的迴歸測試：第一次成功後 DraftStatus 變 Pending，只擋 Processing 會讓
        // 第二次也被放行，同一份逐字稿就生成兩趟、重複計費。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", projectId: project.Id);

        var service = fixture.CreateService();
        var first = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);
        var second = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal([meeting.Id], fixture.DraftQueue.Enqueued);
    }

    [Fact]
    public async Task RequestDraftAsync_ShouldEnqueue_WhenPreviousRunWasCancelled()
    {
        // 取消之後必須能重跑（與轉錄同一個立場）。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var template = await fixture.AddPromptTemplateAsync("標準會議紀錄");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "被取消的會議",
            projectId: project.Id,
            draftStatus: DraftStatus.Cancelled);

        var service = fixture.CreateService();
        var result = await service.RequestDraftAsync(meeting.Id, project.Id, template.Id);

        Assert.True(result.Success);
        Assert.Single(fixture.DraftQueue.Enqueued);
    }
    #endregion

    #region 專案可見性（0.4.99 起取代團隊）

    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public async Task GetAsync_Member_ShouldSeeOwnProjectsAndOwnUnassignedMeetings()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var (userA, _, _) = await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false, userA);

        var result = await service.GetAsync(NewRequest());

        // 別人專案的看不到；沒有上傳者的舊會議在歸屬之前只有管理者看得到。
        Assert.Equal(
            ["A上傳未歸屬", "A專案會議"],
            result.Result.Select(x => x.Title).Order().ToList());
    }

    [Fact]
    public async Task GetAsync_UserWithoutProjects_ShouldSeeNothingOfOthers()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        await fixture.SeedDefaultMeetingsAsync();
        var stranger = await fixture.AddUserAsync("carol");
        var service = fixture.CreateService(isAdmin: false, stranger.Id);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task GetById_NonMember_ShouldDenyMeetingOfOtherProject()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var (userA, _, ids) = await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false, userA);

        var model = await service.GetAsync(ids["B專案會議"]);

        Assert.Equal(0, model.Id);
    }

    [Fact]
    public async Task AddAsync_ShouldRecordUploader()
    {
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice");
        var service = fixture.CreateService(isAdmin: false, user.Id);

        var model = new MeetingAdapterModel { Title = "新會議" };
        var result = await service.AddAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == model.Id);
        Assert.Equal(user.Id, saved.CreatedByUserId);
    }

    [Fact]
    public async Task UpdateAndDelete_NonMember_ShouldBeDenied()
    {
        // 0.4.98 以前這兩支完全沒有權限檢查。
        await using var fixture = await MeetingServiceFixture.CreateAsync();
        var (userA, _, ids) = await fixture.SeedDefaultMeetingsAsync();
        var service = fixture.CreateService(isAdmin: false, userA);

        var update = await service.UpdateAsync(new MeetingAdapterModel { Id = ids["B專案會議"], Title = "改掉別人的標題" });
        var delete = await service.DeleteAsync(ids["B專案會議"]);

        Assert.False(update.Success);
        Assert.False(delete.Success);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == ids["B專案會議"]);
        Assert.Equal("B專案會議", saved.Title);
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

    private sealed class FakeTranscriptionQueue : ITranscriptionQueue
    {
        /// <summary>入列的會議 Id。刻意維持 List&lt;int&gt;，讓既有的 23 處斷言一行都不用改。</summary>
        public List<int> Enqueued { get; } = [];

        /// <summary>完整的請求內容，用來驗證觸發者有沒有一路傳進來（0.4.80）。</summary>
        public List<MeetingJobRequest> Requests { get; } = [];

        public ValueTask EnqueueAsync(MeetingJobRequest request, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(request.MeetingId);
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MeetingJobRequest> DequeueAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("測試不會消費佇列。");
    }

    private sealed class FakeMeetingDraftQueue : IMeetingDraftQueue
    {
        /// <summary>入列的會議 Id。刻意維持 List&lt;int&gt;，讓既有的 23 處斷言一行都不用改。</summary>
        public List<int> Enqueued { get; } = [];

        /// <summary>完整的請求內容，用來驗證觸發者有沒有一路傳進來（0.4.80）。</summary>
        public List<MeetingJobRequest> Requests { get; } = [];

        public ValueTask EnqueueAsync(MeetingJobRequest request, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(request.MeetingId);
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MeetingJobRequest> DequeueAsync(CancellationToken cancellationToken)
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
        private readonly AiChatStore chatStore;

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
            systemSettings.ExternalFileSystem.AiChatPath = AiChatRoot;

            fileStore = new MeetingFileStore(
                Options.Create(systemSettings),
                loggerFactory.CreateLogger<MeetingFileStore>());

            chatStore = new AiChatStore(
                Options.Create(systemSettings),
                loggerFactory.CreateLogger<AiChatStore>());
        }

        public BackendDBContext Context { get; }

        public FakeTranscriptionQueue Queue { get; } = new();

        public FakeMeetingDraftQueue DraftQueue { get; } = new();

        public MeetingDraftProgressNotifier DraftProgressNotifier { get; } = new();

        /// <summary>進度通知器沒有外部相依，直接用真的，順便驗證服務層有把工作登錄進去。</summary>
        public TranscriptionProgressNotifier ProgressNotifier { get; } = new();

        public string MediaRoot => Path.Combine(rootPath, "media");

        public string TranscriptRoot => Path.Combine(rootPath, "transcript");

        public string AiChatRoot => Path.Combine(rootPath, "aichat");

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

        /// <summary>
        /// 入列時要記成哪一位使用者（0.4.80 的用量帳本歸屬）。
        /// 預設是空白的 <c>CurrentUser</c>，與「未登入或背景 scope」的實際狀況一致。
        /// </summary>
        public CurrentUserService CurrentUserService { get; } = new();

        /// <summary>預設是管理者；<paramref name="isAdmin"/> 為 false 時是 Id 為 <paramref name="userId"/> 的一般使用者。</summary>
        public MeetingService CreateService(bool isAdmin = true, int userId = 0)
        {
            return new MeetingService(
                Context,
                mapper,
                loggerFactory.CreateLogger<MeetingService>(),
                isAdmin ? TestProjectAccess.Admin(Context, userId) : TestProjectAccess.User(Context, userId),
                fileStore,
                chatStore,
                Queue,
                ProgressNotifier,
                DraftQueue,
                DraftProgressNotifier,
                CurrentUserService);
        }

        public async Task<Meeting> AddMeetingAsync(
            string title,
            IEnumerable<string>? categories = null,
            int? projectId = null,
            int? createdByUserId = null)
        {
            var meeting = new Meeting
            {
                Title = title,
                Categories = TagStringHelper.ToStored(categories),
                ProjectId = projectId,
                CreatedByUserId = createdByUserId,
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
            int? createdByUserId = null)
        {
            var meeting = new Meeting
            {
                Title = title,
                TranscriptionStatus = TranscriptionStatus.Completed,
                TranscriptRelativePath = $"2026/08/{Guid.NewGuid():N}.txt",
                ProjectId = projectId,
                DraftStatus = draftStatus,
                CreatedByUserId = createdByUserId,
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

        public async Task<PromptTemplate> AddPromptTemplateAsync(string name)
        {
            var template = new PromptTemplate
            {
                Name = name,
                Content = "請根據以下逐字稿整理會議紀錄：{{transcript}}",
                IsEnabled = true,
            };

            Context.PromptTemplate.Add(template);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return template;
        }

        public async Task<MyUser> AddUserAsync(string account)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = true };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        /// <summary>
        /// 讓使用者看得到專案（0.4.102 起靠團隊）：專案以它專屬的團隊當主責，使用者加進那個團隊。
        /// </summary>
        public async Task AddMemberAsync(int projectId, int userId)
        {
            var groupName = $"專案{projectId}的團隊";
            var team = await Context.Team.FirstOrDefaultAsync(x => x.Name == groupName);
            if (team is null)
            {
                team = new Team { Name = groupName, IsEnabled = true };
                Context.Team.Add(team);
                await Context.SaveChangesAsync();
                Context.ProjectTeam.Add(new ProjectTeam { ProjectId = projectId, TeamId = team.Id, IsPrimary = true });
            }

            Context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = team.Id });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        /// <summary>
        /// 專案可見性的標準資料（0.4.102）：使用者 A 在專案 A 的主責團隊、B 在專案 B 的主責團隊；
        /// 另有 A 上傳還沒歸屬的會議，以及沒有上傳者的舊會議。
        /// </summary>
        public async Task<(int UserA, int UserB, Dictionary<string, int> Meetings)> SeedDefaultMeetingsAsync()
        {
            var userA = await AddUserAsync("alice");
            var userB = await AddUserAsync("bob");
            var projectA = await AddProjectAsync("專案A");
            var projectB = await AddProjectAsync("專案B");
            await AddMemberAsync(projectA.Id, userA.Id);
            await AddMemberAsync(projectB.Id, userB.Id);

            var a = await AddMeetingAsync("A專案會議", projectId: projectA.Id);
            var b = await AddMeetingAsync("B專案會議", projectId: projectB.Id);
            var mine = await AddMeetingAsync("A上傳未歸屬", createdByUserId: userA.Id);
            var legacy = await AddMeetingAsync("舊會議未歸屬");

            return (userA.Id, userB.Id, new Dictionary<string, int>
            {
                ["A專案會議"] = a.Id,
                ["B專案會議"] = b.Id,
                ["A上傳未歸屬"] = mine.Id,
                ["舊會議未歸屬"] = legacy.Id,
            });
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

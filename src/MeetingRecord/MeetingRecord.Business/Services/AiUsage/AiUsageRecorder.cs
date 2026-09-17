using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Models.Others;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 一次呼叫要記進帳本的全部事實。金額與單價由 <see cref="AiUsageRecorder"/> 依
/// <paramref name="Model"/> 查表補上，呼叫端不必知道定價。
/// </summary>
/// <param name="Tokens">供應商回報的 token 用量；null 代表沒回報（失敗、取消、或不支援）。</param>
/// <param name="AudioSeconds">這一段音訊的秒數（僅語音轉錄）。</param>
public sealed record AiUsageEntry(
    AiUsageFeature Feature,
    AiUsageOutcome Outcome,
    string Provider,
    string Model,
    AiTokenUsage? Tokens = null,
    double? AudioSeconds = null,
    bool IsAudioDurationEstimated = false,
    int? UserId = null,
    string? UserName = null,
    int? MeetingId = null,
    int? ProjectId = null,
    string? ErrorMessage = null);

/// <summary>
/// 供應商回報的 token 用量。
/// <paramref name="CachedInputTokens"/> 已經**含在** <paramref name="InputTokens"/> 裡，不可再加一次。
/// </summary>
public sealed record AiTokenUsage(int InputTokens, int OutputTokens, int? CachedInputTokens = null);

/// <summary>
/// 把 <see cref="CurrentUser"/> 轉成帳本要的歸屬欄位。
///
/// <para>
/// 三個入口（AI 問答、待辦擷取、以及兩個佇列的 enqueue 點）都走這一支，
/// 免得「誰用了多少」在不同來源有不同的判斷方式。
/// 顯示名稱的優先序與 <c>AiChatService.ResolveCurrentUserName</c> 一致：姓名 → 帳號 → null。
/// </para>
///
/// <para>
/// ⚠️ 未登入或背景 scope 拿到的是一個**空白的** <see cref="CurrentUser"/>（Id=0、Name=""），
/// 不是 null。所以判斷要看 <c>Id > 0</c> 而不是 null 檢查。
/// </para>
/// </summary>
public static class AiUsageAttribution
{
    public static (int? UserId, string? UserName) Resolve(CurrentUser? user)
    {
        if (user is null || user.Id <= 0)
        {
            return (null, null);
        }

        var name = !string.IsNullOrWhiteSpace(user.Name)
            ? user.Name
            : !string.IsNullOrWhiteSpace(user.Account)
                ? user.Account
                : null;

        return (user.Id, name);
    }
}

/// <summary>
/// 把一次 AI 呼叫寫進用量帳本。
///
/// <para>
/// 註冊為 <b>Scoped</b>（需要 <see cref="BackendDBContext"/>）。背景服務為每筆工作自建 scope，
/// job runner 在該 scope 內解析，所以背景路徑也用得到。
/// </para>
///
/// <para>
/// ⚠️ 刻意**不注入 CurrentUserService**：它是 Scoped，在背景工作的 scope 裡會解析成一個
/// 空白的 CurrentUser（Id=0、Name=""），於是所有背景呼叫都被記到「空白使用者」而且不報錯。
/// 使用者一律由呼叫端明確傳進來。
/// </para>
/// </summary>
public class AiUsageRecorder
{
    /// <summary>錯誤訊息的保留長度。帳本不是 log，不需要完整堆疊。</summary>
    private const int ErrorMessageMaxLength = 500;

    private readonly BackendDBContext context;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<AiUsageRecorder> logger;

    public AiUsageRecorder(
        BackendDBContext context,
        IOptions<LlmSettings> llmSettings,
        ILogger<AiUsageRecorder> logger)
    {
        this.context = context;
        this.llmSettings = llmSettings;
        this.logger = logger;
    }

    /// <summary>
    /// 寫一列帳。
    ///
    /// <para>
    /// ⚠️ <b>永遠不拋例外。</b> 帳本寫不進去是要記錄下來的事故，不是讓會議紀錄生成失敗的理由——
    /// 使用者的工作不該因為記帳失敗而毀掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 一律以 <see cref="CancellationToken.None"/> 存檔。使用者取消時傳進來的權杖
    /// 已經是 cancelled 狀態，帶進去等於這一筆寫不進去——而**取消的呼叫照樣花了錢**，
    /// 正是最需要記下來的那一種。（同 <c>MeetingDraftJobRunner.MarkCancelledAsync</c> 的理由。）
    /// </para>
    ///
    /// <para>
    /// ⚠️ 呼叫端必須在「修改自己的實體欄位<b>之前</b>」呼叫本方法。
    /// <see cref="BackendDBContext"/> 是同一個 scope 共用的，這裡的 SaveChanges 會把呼叫端
    /// 所有還沒打算提交的追蹤變更一起送出去。
    /// </para>
    /// </summary>
    public async Task RecordAsync(AiUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            var settings = llmSettings.Value;
            settings.Pricing.TryGetValue(entry.Model ?? string.Empty, out var price);

            var isTokenBased = AiUsageFeatureText.IsTokenBased(entry.Feature);

            context.AiUsageLog.Add(new AiUsageLog
            {
                OccurredAt = DateTime.Now,
                Feature = entry.Feature,
                Outcome = entry.Outcome,
                Provider = entry.Provider ?? string.Empty,
                Model = entry.Model ?? string.Empty,

                // 計費單位由功能唯一決定，另一組一律留 null——兩組並存必然會出現互相矛盾的資料。
                InputTokens = isTokenBased ? entry.Tokens?.InputTokens : null,
                OutputTokens = isTokenBased ? entry.Tokens?.OutputTokens : null,
                CachedInputTokens = isTokenBased ? entry.Tokens?.CachedInputTokens : null,
                AudioSeconds = isTokenBased ? null : entry.AudioSeconds,
                IsAudioDurationEstimated = !isTokenBased && entry.IsAudioDurationEstimated,

                EstimatedCost = isTokenBased
                    ? AiUsagePricing.EstimateTokenCost(entry.Tokens?.InputTokens, entry.Tokens?.OutputTokens, price)
                    : AiUsagePricing.EstimateAudioCost(entry.AudioSeconds, price),
                Currency = settings.Currency,
                InputPricePerMillion = price?.InputPerMillionTokens,
                OutputPricePerMillion = price?.OutputPerMillionTokens,
                AudioPricePerMinute = price?.AudioPerMinute,

                UserId = entry.UserId,
                UserName = entry.UserName,
                MeetingId = entry.MeetingId,
                ProjectId = entry.ProjectId,
                ErrorMessage = Truncate(entry.ErrorMessage, ErrorMessageMaxLength),
            });

            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to write AI usage ledger. Feature={Feature}, Outcome={Outcome}, MeetingId={MeetingId}",
                entry.Feature,
                entry.Outcome,
                entry.MeetingId);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

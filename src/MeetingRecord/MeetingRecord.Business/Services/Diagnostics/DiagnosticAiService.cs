using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Diagnostics;

/// <summary>一次診斷分析的結果。</summary>
/// <param name="Content">模型回覆的 Markdown。</param>
/// <param name="Usage">token 用量；供應商沒回報時為 null。</param>
/// <param name="ModelName">實際使用的 deployment／model。</param>
/// <param name="Elapsed">從送出到收完的時間。</param>
public sealed record DiagnosticAiResult(string Content, AiTokenUsage? Usage, string ModelName, TimeSpan Elapsed);

/// <summary>
/// 「監控與診斷」的 AI 分析共用入口（0.4.121：AI 日誌分析；之後的例外分析也走這裡）。
///
/// <para>形狀比照 <c>TodoExtractionService</c>：依 <c>LlmSettings.DefaultProvider</c> 挑供應商、
/// 呼叫 <see cref="ITextGenerationProvider"/>、**成功／失敗／取消都寫進用量帳本**——帳本要求涵蓋全部付費呼叫。
/// 不另接一條到 Azure OpenAI 的路徑。</para>
///
/// <para>⚠️ 這支會花錢、也會把日誌內容送出去：呼叫端（畫面）必須先跳「（會產生費用）」確認窗。</para>
/// </summary>
public class DiagnosticAiService
{
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly AiUsageRecorder usageRecorder;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<DiagnosticAiService> logger;

    public DiagnosticAiService(
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        IOptions<LlmSettings> llmSettings,
        AiUsageRecorder usageRecorder,
        CurrentUserService currentUserService,
        ILogger<DiagnosticAiService> logger)
    {
        this.textGenerationProviders = textGenerationProviders;
        this.llmSettings = llmSettings;
        this.usageRecorder = usageRecorder;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>是否能呼叫 AI。畫面用它決定 AI 按鈕要不要停用。</summary>
    public bool IsAvailable => string.IsNullOrEmpty(UnavailableReason);

    /// <summary>不能呼叫時的原因（給畫面的 Tooltip）；可以呼叫時為空字串。</summary>
    public string UnavailableReason
    {
        get
        {
            try
            {
                ResolveProvider();
                return string.Empty;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// 送出一次分析並等到完成。
    /// </summary>
    /// <param name="onDelta">串流時逐段回報新增的文字（不是累積後的全文）；不需要時傳 null。</param>
    /// <exception cref="OperationCanceledException">呼叫端取消；帳本已記為 Cancelled。</exception>
    /// <exception cref="InvalidOperationException">未設定供應商。</exception>
    public async Task<DiagnosticAiResult> AnalyzeAsync(
        AiUsageFeature feature,
        string systemPrompt,
        string userPrompt,
        Action<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider();
        var started = DateTime.UtcNow;

        TextGenerationResult generated;
        try
        {
            generated = await provider.GenerateAsync(systemPrompt, userPrompt, onDelta, cancellationToken);
        }
        catch (Exception ex)
        {
            var outcome = ex is OperationCanceledException ? AiUsageOutcome.Cancelled : AiUsageOutcome.Failed;
            await RecordUsageAsync(feature, provider, outcome, tokens: null, errorMessage: ex.Message);
            throw;
        }

        await RecordUsageAsync(feature, provider, AiUsageOutcome.Succeeded, generated.Usage, errorMessage: null);

        var elapsed = DateTime.UtcNow - started;
        logger.LogInformation(
            "Diagnostic AI analysis completed. Feature={Feature}, Model={Model}, PromptChars={PromptChars}, ResponseChars={ResponseChars}, ElapsedMs={ElapsedMs}",
            feature,
            provider.ModelName,
            userPrompt.Length,
            generated.Content.Length,
            (long)elapsed.TotalMilliseconds);

        return new DiagnosticAiResult(generated.Content, generated.Usage, provider.ModelName, elapsed);
    }

    /// <summary>把這次呼叫寫進用量帳本。這條路徑走在使用者的請求裡，所以拿得到 CurrentUserService。</summary>
    private Task RecordUsageAsync(
        AiUsageFeature feature,
        ITextGenerationProvider provider,
        AiUsageOutcome outcome,
        AiTokenUsage? tokens,
        string? errorMessage)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);

        return usageRecorder.RecordAsync(new AiUsageEntry(
            feature,
            outcome,
            provider.ProviderName,
            provider.ModelName,
            Tokens: tokens,
            UserId: userId,
            UserName: userName,
            ErrorMessage: errorMessage));
    }

    /// <summary>依 <c>LlmSettings.DefaultProvider</c> 挑出供應商，形狀比照 <c>TodoExtractionService</c>。</summary>
    private ITextGenerationProvider ResolveProvider()
    {
        var settings = llmSettings.Value;
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                $"尚未設定文字生成供應商，請在 {LlmSettings.SectionName}:DefaultProvider 指定。");
        }

        var name = settings.DefaultProvider.Trim();
        var provider = textGenerationProviders
            .FirstOrDefault(x => string.Equals(x.ProviderName, name, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            var supported = string.Join("、", textGenerationProviders.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"找不到名為「{name}」的文字生成供應商實作。目前支援：{supported}。");
        }

        return provider;
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.HelpAssistant;

/// <summary>一次回答。</summary>
public sealed record HelpAssistantAnswer(string Content, string ModelName);

/// <summary>
/// 使用說明 AI 助理（1.0.43）：把使用說明連同問題送給 AI，回答操作上的問題。
///
/// <para>
/// 形狀照抄 <c>DiagnosticAiService</c>：依 <c>LlmSettings.DefaultProvider</c> 挑供應商，
/// 經 <see cref="AiCallGateway"/> 呼叫——用量上限、Token 用量帳本、AI 對話紀錄都由 gateway 處理，不可繞過。
/// </para>
///
/// <para>
/// 使用說明全文由呼叫端傳進來（它是 Web 組件的內嵌資源，Business 讀不到）；
/// 要不要含「管理員」章節也由呼叫端決定（一般使用者不送，使用者決定）。
/// 對話不存檔：只存在說明頁上，重新整理就清空。
/// </para>
///
/// <para>
/// 刻意不逐題跳費用確認（使用者決定，比照 AI 問答）：畫面上常駐一行費用說明。
/// </para>
/// </summary>
public class HelpAssistantService
{
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly AiCallGateway gateway;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<HelpAssistantService> logger;

    public HelpAssistantService(
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        IOptions<LlmSettings> llmSettings,
        AiCallGateway gateway,
        CurrentUserService currentUserService,
        ILogger<HelpAssistantService> logger)
    {
        this.textGenerationProviders = textGenerationProviders;
        this.llmSettings = llmSettings;
        this.gateway = gateway;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>是否能呼叫 AI。畫面用它決定輸入框要不要停用。</summary>
    public bool IsAvailable => string.IsNullOrEmpty(UnavailableReason);

    /// <summary>不能呼叫時的原因；可以呼叫時為空字串。</summary>
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

    /// <summary>提問並等到回答完成；<paramref name="onDelta"/> 逐段回報新增的文字。</summary>
    /// <param name="threadKey">把同一頁上的連續提問串在一起（AI 對話紀錄用）。</param>
    /// <exception cref="OperationCanceledException">使用者按「停止」；帳本已記為 Cancelled。</exception>
    /// <exception cref="AiQuotaExceededException">已達 AI 用量上限（沒有送出）。</exception>
    /// <exception cref="InvalidOperationException">未設定供應商。</exception>
    public async Task<HelpAssistantAnswer> AskAsync(
        string manual,
        IReadOnlyList<HelpAssistantTurn> history,
        string question,
        Action<string>? onDelta,
        CancellationToken cancellationToken,
        string? threadKey = null)
    {
        var userPrompt = HelpAssistantPromptBuilder.BuildUserPrompt(manual, history, question);
        var provider = ResolveProvider();

        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
        var generated = await gateway.GenerateAsync(
            provider,
            new AiCallContext(
                AiUsageFeature.HelpAssistant,
                userId,
                userName,
                RelatedInfo: HelpAssistantPromptBuilder.BuildRelatedInfo(question),
                ThreadKey: threadKey),
            HelpAssistantPromptBuilder.SystemPrompt,
            userPrompt,
            onDelta,
            cancellationToken);

        logger.LogInformation(
            "Help assistant answered. Model={Model}, PromptChars={PromptChars}, HistoryTurns={HistoryTurns}, AnswerChars={AnswerChars}",
            provider.ModelName,
            userPrompt.Length,
            history.Count,
            generated.Content.Length);

        return new HelpAssistantAnswer(generated.Content, provider.ModelName);
    }

    /// <summary>依 <c>LlmSettings.DefaultProvider</c> 挑出供應商，形狀比照 <c>DiagnosticAiService</c>。</summary>
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

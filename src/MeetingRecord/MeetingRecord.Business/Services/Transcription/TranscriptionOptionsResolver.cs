using Microsoft.Extensions.Options;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>驗證過的轉錄選項，寫進會議（<c>Meeting.TranscriptionEngine</c> 等欄位）。</summary>
/// <param name="Diarize">null＝沒有指定，照設定的部署本身行為（1.1.72 以前）。</param>
public sealed record ResolvedTranscriptionOptions(TranscriptionEngine Engine, bool? Diarize, int? MaxSpeakers);

/// <summary>
/// 1.1.73：上傳／重新轉錄時選的轉錄方式——畫面要畫哪些選項、選的組合能不能用、Azure OpenAI 實際打哪個部署、用哪個單價。
/// 驗證放在入列之前（上傳工作階段建立、重新轉錄），使用者當場看到原因，不用等背景工作失敗。
/// </summary>
public sealed class TranscriptionOptionsResolver(IOptions<LlmSettings> llmSettings, IOptions<TranscriptionSettings> transcriptionSettings)
{
    public TranscriptionOptionsMetaDto GetMeta(int? participantCount)
    {
        var settings = transcriptionSettings.Value;
        var openAiDiarize = ResolveOpenAiDeployment(true) is not null;
        var openAiPlain = ResolveOpenAiDeployment(false) is not null;
        var llmSpeech = settings.AzureSpeech.IsConfigured;

        var defaultEngine = Enum.TryParse<TranscriptionEngine>(settings.DefaultEngine?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : TranscriptionEngine.AzureOpenAI;

        // 預設的引擎沒設定好時改選另一個，畫面才不會一打開就是不能用的組合。
        if (defaultEngine == TranscriptionEngine.AzureLlmSpeech && !llmSpeech)
        {
            defaultEngine = TranscriptionEngine.AzureOpenAI;
        }
        else if (defaultEngine == TranscriptionEngine.AzureOpenAI && !openAiDiarize && !openAiPlain && llmSpeech)
        {
            defaultEngine = TranscriptionEngine.AzureLlmSpeech;
        }

        var defaultDiarize = settings.DefaultDiarize;
        if (defaultEngine == TranscriptionEngine.AzureOpenAI && !(defaultDiarize ? openAiDiarize : openAiPlain))
        {
            defaultDiarize = !defaultDiarize;
        }

        return new TranscriptionOptionsMetaDto
        {
            OpenAiDiarizeAvailable = openAiDiarize,
            OpenAiPlainAvailable = openAiPlain,
            LlmSpeechAvailable = llmSpeech,
            DefaultEngine = (int)defaultEngine,
            DefaultDiarize = defaultDiarize,
            SuggestedMaxSpeakers = ClampSpeakers(participantCount is > 0 ? participantCount.Value : settings.DefaultMaxSpeakers),
            MinSpeakers = AzureSpeechSettings.MinSpeakers,
            MaxSpeakers = AzureSpeechSettings.MaxSpeakers,
            LlmSpeechMaxFileSeconds = settings.AzureSpeech.MaxFileSeconds,
        };
    }

    /// <summary>
    /// 驗證使用者選的組合。<paramref name="options"/> 為 null（API 沒帶）＝沿用設定的部署，一律放行。
    /// </summary>
    /// <returns>可以用回 (結果, null)；不行回 (null, 原因)。</returns>
    public (ResolvedTranscriptionOptions? Options, string? Error) Resolve(TranscriptionOptionsDto? options)
    {
        if (options is null)
        {
            return (new ResolvedTranscriptionOptions(TranscriptionEngine.AzureOpenAI, null, null), null);
        }

        if (!Enum.IsDefined(typeof(TranscriptionEngine), options.Engine))
        {
            return (null, "轉錄引擎不正確。");
        }

        var engine = (TranscriptionEngine)options.Engine;
        if (engine == TranscriptionEngine.AzureLlmSpeech)
        {
            if (!transcriptionSettings.Value.AzureSpeech.IsConfigured)
            {
                return (null, "系統尚未設定 Azure LLM Speech（Endpoint 與 ApiKey），請改選 Azure OpenAI 或洽管理者。");
            }

            if (!options.Diarize)
            {
                return (new ResolvedTranscriptionOptions(engine, false, null), null);
            }

            if (options.MaxSpeakers is not { } maxSpeakers
                || maxSpeakers < AzureSpeechSettings.MinSpeakers
                || maxSpeakers > AzureSpeechSettings.MaxSpeakers)
            {
                return (null, $"講者上限要在 {AzureSpeechSettings.MinSpeakers}～{AzureSpeechSettings.MaxSpeakers} 之間。");
            }

            return (new ResolvedTranscriptionOptions(engine, true, maxSpeakers), null);
        }

        if (ResolveOpenAiDeployment(options.Diarize) is null)
        {
            return (null, options.Diarize
                ? "Azure OpenAI 目前設定的轉錄部署不會分講者，請改選不分講者或 Azure LLM Speech。"
                : "Azure OpenAI 沒有設定不分講者的轉錄部署（LlmSettings:Providers:*:TranscriptionModelNoSpeakers），請改選分講者或 Azure LLM Speech。");
        }

        return (new ResolvedTranscriptionOptions(engine, options.Diarize, null), null);
    }

    /// <summary>
    /// Azure OpenAI 實際要打的部署。<paramref name="diarize"/> 為 null＝設定的轉錄部署；
    /// true 要求部署會分講者、false 要求不會（設定的是 diarize 時改用 <see cref="LlmProviderSettings.TranscriptionModelNoSpeakers"/>）。
    /// 沒有合用的部署回 null。
    /// </summary>
    public string? ResolveOpenAiDeployment(bool? diarize)
    {
        var provider = llmSettings.Value.GetTranscriptionProvider();
        var main = provider?.TranscriptionModel?.Trim();
        if (string.IsNullOrWhiteSpace(main))
        {
            return null;
        }

        var mainDiarizes = AzureOpenAiTranscriptionProvider.IsDiarizeModel(main);
        return diarize switch
        {
            null => main,
            true => mainDiarizes ? main : null,
            false when !mainDiarizes => main,
            false => string.IsNullOrWhiteSpace(provider!.TranscriptionModelNoSpeakers) ? null : provider.TranscriptionModelNoSpeakers.Trim(),
        };
    }

    /// <summary>估算費用要用的單價鍵：LLM Speech 用固定鍵，Azure OpenAI 用實際打的部署。</summary>
    public string? PricingModel(TranscriptionEngine engine, bool? diarize) =>
        engine == TranscriptionEngine.AzureLlmSpeech ? AzureSpeechSettings.PricingModel : ResolveOpenAiDeployment(diarize);

    /// <summary>LLM Speech 的片語清單：專案名稱＋專案與會人員名冊。</summary>
    public static IReadOnlyList<string> BuildPhrases(string? projectTitle, string? storedParticipants)
    {
        var phrases = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectTitle))
        {
            phrases.Add(projectTitle.Trim());
        }

        phrases.AddRange(TagStringHelper.ToList(storedParticipants));
        return phrases;
    }

    private static int ClampSpeakers(int value) => Math.Clamp(value, AzureSpeechSettings.MinSpeakers, AzureSpeechSettings.MaxSpeakers);
}

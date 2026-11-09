using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Web.Testing;

/// <summary>
/// 端對端測試用的假 AI（1.0.61）。**只在環境名稱是 <see cref="EnvironmentName"/> 時註冊**（Program.cs），
/// 讓 Playwright 測試能跑完轉錄、產生會議紀錄、AI 問答、待辦抽取，卻絕不打到付費的 Azure OpenAI。
///
/// <para>
/// 仍然經過 <c>AiCallGateway</c>，所以用量帳本與 AI 對話紀錄也一起被測到。
/// 啟用方式：<c>--environment E2E --LlmSettings:DefaultProvider=Fake --LlmSettings:TranscriptionProvider=Fake</c>
/// 加上 <c>LlmSettings:Providers:Fake</c> 的佔位設定（ApiKey／Endpoint／Model／TranscriptionModel）。
/// Production 的啟動安全檢查會拒絕供應商名稱 <see cref="ProviderName"/>。
/// </para>
/// </summary>
public static class E2EFakeAi
{
    public const string EnvironmentName = "E2E";

    public const string ProviderName = "Fake";

    /// <summary>假轉錄回傳的逐字稿；測試用它確認轉錄真的跑完。</summary>
    public const string Transcript = "E2E 假逐字稿：主持人：今天確認需求範圍。工程師：下週五前完成介面設計。";

    /// <summary>假會議紀錄／問答回應的開頭；測試用它確認串流內容真的顯示出來。</summary>
    public const string AnswerMarker = "E2E 假 AI 回應";

    /// <summary>假待辦抽取回傳的待辦標題。</summary>
    public const string ExtractedTodoTitle = "E2E 假待辦：完成介面設計";

    public static IServiceCollection AddE2EFakeAi(this IServiceCollection services)
    {
        services.AddScoped<ITextGenerationProvider, FakeTextGenerationProvider>();
        services.AddScoped<ITranscriptionProvider, FakeTranscriptionProvider>();

        // 假轉檔：不需要 FFmpeg，也不需要真的音檔（測試上傳的是隨機位元組）。
        services.AddScoped<IMediaConverter, FakeMediaConverter>();
        return services;
    }

    private sealed class FakeTextGenerationProvider : ITextGenerationProvider
    {
        public string ProviderName => E2EFakeAi.ProviderName;

        public string ModelName => "fake";

        public async Task<TextGenerationResult> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            Action<string>? onDelta,
            CancellationToken cancellationToken,
            IReadOnlyList<PromptImage>? images = null)
        {
            // 待辦抽取要求「只回傳一個 JSON 陣列」，其他功能回一段 Markdown。
            var content = (systemPrompt + userPrompt).Contains("JSON 陣列", StringComparison.Ordinal)
                ? $$"""[{"title":"{{ExtractedTodoTitle}}","description":"","owner":"","dueDate":null,"priority":"中"}]"""
                : $"## {AnswerMarker}\n\n- 這是端對端測試的固定內容，沒有呼叫任何付費 AI。\n- 下週五前完成介面設計。\n";

            // 分幾段回呼，模擬串流逐字顯示。
            foreach (var piece in content.Chunk(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                onDelta?.Invoke(new string(piece));
                await Task.Delay(10, cancellationToken);
            }

            return new TextGenerationResult(content, null);
        }
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public string ProviderName => E2EFakeAi.ProviderName;

        public string ModelName => "fake";

        public Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
            => Task.FromResult(Transcript);
    }

    private sealed class FakeMediaConverter : IMediaConverter
    {
        public Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken)
        {
            var workingDirectory = Path.Combine(Path.GetTempPath(), "MeetingRecord", "e2e-transcode", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            var segment = Path.Combine(workingDirectory, "part-000.mp3");
            File.WriteAllBytes(segment, [0xFF, 0xFB, 0x90, 0x00]);
            return Task.FromResult(new MediaSegmentSet(workingDirectory, [segment], TimeSpan.FromSeconds(90)));
        }
    }
}

using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Tests;

/// <summary>
/// 串流回應裡的 token 用量解析（0.4.80）。
///
/// <para>
/// ⚠️ <b>這是整個用量帳本最容易錯的一點。</b> 帶用量的那一片，它的 <c>choices</c> 是**空陣列**，
/// 所以走不到 delta 那條路；0.4.80 之前的 <c>ExtractDelta</c> 取不到東西就 <c>continue</c>，
/// 用量會被**靜默丟棄**——一切照常運作、圖表照畫，只是 token 永遠是空的，沒有任何錯誤訊息。
/// </para>
///
/// <para>
/// 下面的樣本是**實際打 Azure OpenAI 取回的原文**（api-version 2024-10-21，
/// 串流帶 <c>stream_options.include_usage</c>），只縮短了無關欄位，不是想像出來的格式。
/// </para>
/// </summary>
public sealed class AzureOpenAiUsageParsingTests
{
    /// <summary>實測：帶用量的那一片，choices 是空陣列。</summary>
    private const string UsageChunk =
        """
        {"choices":[],"created":1789616310,"id":"chatcmpl-EOxF8oNMa4zIdj2Rg9ZEbOxdAJ6D9","model":"gpt-5.6-sol-2026-07-09","object":"chat.completion.chunk","service_tier":"default","system_fingerprint":null,"usage":{"completion_tokens":4,"completion_tokens_details":{"accepted_prediction_tokens":0,"audio_tokens":0,"reasoning_tokens":0,"rejected_prediction_tokens":0},"prompt_tokens":8,"prompt_tokens_details":{"audio_tokens":0,"cache_write_tokens":0,"cached_tokens":0},"total_tokens":12}}
        """;

    /// <summary>實測：finish_reason 那一片明確帶 "usage": null。</summary>
    private const string FinishChunk =
        """
        {"choices":[{"content_filter_results":{},"delta":{},"finish_reason":"stop","index":0,"logprobs":null}],"created":1789616310,"id":"chatcmpl-EOxF8oNMa4zIdj2Rg9ZEbOxdAJ6D9","model":"gpt-5.6-sol-2026-07-09","object":"chat.completion.chunk","system_fingerprint":null,"usage":null}
        """;

    /// <summary>實測：一般的內容片。</summary>
    private const string ContentChunk =
        """
        {"choices":[{"content_filter_results":{},"delta":{"content":"OK","refusal":null},"finish_reason":null,"index":0,"logprobs":null}],"created":1789616310,"id":"chatcmpl-EOxF8oNMa4zIdj2Rg9ZEbOxdAJ6D9","model":"gpt-5.6-sol-2026-07-09","object":"chat.completion.chunk","usage":null}
        """;

    [Fact]
    public void ExtractUsage_ShouldReadTokensFromTheFinalChunk()
    {
        var usage = AzureOpenAiTextGenerationProvider.ExtractUsage(UsageChunk);

        Assert.NotNull(usage);
        Assert.Equal(8, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
    }

    [Fact]
    public void ExtractUsage_ShouldReturnNull_ForChunksThatCarryExplicitNullUsage()
    {
        // ⚠️ 開了 include_usage 之後，中間每一片都會明確帶 "usage": null。
        // 所以不能用「JSON 裡有 usage 這個欄位」當成「這是最後一片」的判斷依據。
        Assert.Null(AzureOpenAiTextGenerationProvider.ExtractUsage(ContentChunk));
        Assert.Null(AzureOpenAiTextGenerationProvider.ExtractUsage(FinishChunk));
    }

    [Fact]
    public void ExtractDelta_ShouldStillReturnNull_ForTheUsageChunk()
    {
        // 用量那一片沒有 delta——這正是它在 0.4.80 之前被 continue 吃掉的原因。
        // 這一筆確保新增的解析路徑沒有把 delta 的語意弄髒。
        Assert.Null(AzureOpenAiTextGenerationProvider.ExtractDelta(UsageChunk));
        Assert.Equal("OK", AzureOpenAiTextGenerationProvider.ExtractDelta(ContentChunk));
    }

    [Fact]
    public void ExtractUsage_ShouldReadCachedTokens_WhenPromptCacheIsHit()
    {
        // 快取命中的輸入 token 已經**含在** prompt_tokens 裡，不可再加一次。
        const string cached =
            """
            {"choices":[],"usage":{"completion_tokens":40,"prompt_tokens":1200,"prompt_tokens_details":{"cached_tokens":1024},"total_tokens":1240}}
            """;

        var usage = AzureOpenAiTextGenerationProvider.ExtractUsage(cached);

        Assert.NotNull(usage);
        Assert.Equal(1200, usage.InputTokens);
        Assert.Equal(1024, usage.CachedInputTokens);
    }

    [Fact]
    public void ExtractUsage_ShouldToleratePayloadsWithoutPromptTokenDetails()
    {
        // 不是每個供應商／版本都回 prompt_tokens_details。少了它不該整筆解析失敗。
        const string minimal = """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20}}""";

        var usage = AzureOpenAiTextGenerationProvider.ExtractUsage(minimal);

        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Null(usage.CachedInputTokens);
    }
}

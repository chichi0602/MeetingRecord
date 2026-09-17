using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 串流回應的**整條讀取迴圈**（0.4.80）。用假的 HttpMessageHandler 餵入真實錄下來的
/// SSE 內容，不發任何網路請求。
///
/// <para>
/// ⚠️ 為什麼非要測到迴圈這一層：帶用量的那一片沒有 delta，所以它會走到
/// 「delta 是空的 → continue」那條路。**取用量的程式碼若放在那個 continue 之後，
/// 用量就永遠拿不到**——而單獨測 <c>ExtractUsage</c> 完全看不出這件事，
/// 用假 provider 的上層測試也一樣看不出來。這個檔案補的就是那個缺口。
/// </para>
/// </summary>
public sealed class AzureOpenAiStreamingTests
{
    /// <summary>實際打 Azure OpenAI 錄下來的回應（api-version 2024-10-21，含 include_usage）。</summary>
    private const string RealSseResponse =
        """
        data: {"choices":[{"delta":{"content":"","role":"assistant"},"finish_reason":null,"index":0}],"object":"chat.completion.chunk","usage":null}

        data: {"choices":[{"delta":{"content":"O"},"finish_reason":null,"index":0}],"object":"chat.completion.chunk","usage":null}

        data: {"choices":[{"delta":{"content":"K"},"finish_reason":null,"index":0}],"object":"chat.completion.chunk","usage":null}

        data: {"choices":[{"delta":{},"finish_reason":"stop","index":0}],"object":"chat.completion.chunk","usage":null}

        data: {"choices":[],"object":"chat.completion.chunk","usage":{"completion_tokens":4,"completion_tokens_details":{"reasoning_tokens":0},"prompt_tokens":8,"prompt_tokens_details":{"cached_tokens":0},"total_tokens":12}}

        data: [DONE]

        """;

    [Fact]
    public async Task GenerateAsync_ShouldReturnBothContentAndUsage()
    {
        var (provider, handler) = CreateProvider(RealSseResponse);

        var result = await provider.GenerateAsync("系統", "問題", null, CancellationToken.None);

        Assert.Equal("OK", result.Content);

        // ⚠️ 這個斷言就是整個功能的核心。用量那一片的 choices 是空陣列，
        // 取用量的程式碼放錯位置（在 delta 的 continue 之後）時，這裡會是 null。
        Assert.NotNull(result.Usage);
        Assert.Equal(8, result.Usage.InputTokens);
        Assert.Equal(4, result.Usage.OutputTokens);

        // 順便確認請求真的有要求回傳用量——沒送 stream_options 的話伺服器根本不會回。
        Assert.Contains("\"stream_options\"", handler.LastRequestBody);
        Assert.Contains("\"include_usage\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task GenerateAsync_ShouldSucceedWithoutUsage_WhenServerNeverSendsIt()
    {
        // 伺服器沒送 [DONE] 就關連線、或 api-version 不支援 include_usage 時會這樣。
        // ⚠️ 這**不能**變成例外——那會把「生成成功但拿不到帳」升級成「生成失敗」。
        const string withoutUsage =
            """
            data: {"choices":[{"delta":{"content":"OK"},"index":0}],"object":"chat.completion.chunk"}

            data: [DONE]

            """;

        var (provider, _) = CreateProvider(withoutUsage);

        var result = await provider.GenerateAsync("系統", "問題", null, CancellationToken.None);

        Assert.Equal("OK", result.Content);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task GenerateAsync_ShouldStillFail_WhenNoContentAtAll()
    {
        // 內容過濾或模型無輸出。這仍然必須是失敗，不能靜靜寫入空草稿——
        // 加了用量解析之後這條既有規則不可以被弄壞。
        const string empty =
            """
            data: {"choices":[],"object":"chat.completion.chunk","usage":{"prompt_tokens":8,"completion_tokens":0}}

            data: [DONE]

            """;

        var (provider, _) = CreateProvider(empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GenerateAsync("系統", "問題", null, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_ShouldReportDeltasInOrder()
    {
        var (provider, _) = CreateProvider(RealSseResponse);

        var deltas = new List<string>();
        await provider.GenerateAsync("系統", "問題", deltas.Add, CancellationToken.None);

        // 空字串的第一片不該被回報（既有行為），內容片依序回報。
        Assert.Equal(["O", "K"], deltas);
    }

    private static (AzureOpenAiTextGenerationProvider Provider, StubHandler Handler) CreateProvider(string sse)
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://contoso.openai.azure.com/",
            ApiKey = "test-key",
            Model = "gpt-4o-mini",
            ApiVersion = "2024-10-21",
        };

        var handler = new StubHandler(sse);
        var factory = new StubHttpClientFactory(handler);

        var provider = new AzureOpenAiTextGenerationProvider(
            factory,
            Options.Create(settings),
            LoggerFactory.Create(_ => { }).CreateLogger<AzureOpenAiTextGenerationProvider>());

        return (provider, handler);
    }

    private sealed class StubHandler(string sse) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

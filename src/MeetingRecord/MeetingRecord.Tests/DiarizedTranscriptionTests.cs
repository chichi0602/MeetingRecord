using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.63 轉錄改用 gpt-4o-transcribe-diarize：請求參數、diarized_json 轉成「講者 A：」逐字稿、
/// 講者代號的判斷與截斷估算。
/// </summary>
public sealed class DiarizedTranscriptionTests
{
    #region DiarizedTranscriptFormatter

    [Fact]
    public void Format_ShouldMergeAdjacentSameSpeakerIntoOneLine()
    {
        var json = """
            {"text":"大家好今天開會好的","segments":[
              {"speaker":"A","text":"大家好","start":0,"end":1},
              {"speaker":"A","text":"今天開會","start":1,"end":2},
              {"speaker":"B","text":"好的","start":2,"end":3},
              {"speaker":"A","text":"開始吧","start":3,"end":4}
            ]}
            """;

        Assert.Equal("講者 A：大家好今天開會\n講者 B：好的\n講者 A：開始吧", DiarizedTranscriptFormatter.Format(json));
    }

    [Fact]
    public void Format_ShouldNeverUseBlankLineBetweenSpeakers()
    {
        // \n\n 是轉錄分段的邊界，TranscriptChunker 與雜訊過濾都靠它切段。
        var json = """{"segments":[{"speaker":"A","text":"一"},{"speaker":"B","text":"二"}]}""";

        Assert.DoesNotContain("\n\n", DiarizedTranscriptFormatter.Format(json));
    }

    [Fact]
    public void Format_ShouldSpaceEnglishWordsWhenMerging()
    {
        var json = """{"segments":[{"speaker":"A","text":"hello"},{"speaker":"A","text":"world"}]}""";

        Assert.Equal("講者 A：hello world", DiarizedTranscriptFormatter.Format(json));
    }

    [Fact]
    public void Format_ShouldLabelMissingSpeakerAsUnknown_AndSkipEmptyText()
    {
        var json = """{"segments":[{"text":"沒有講者"},{"speaker":"B","text":"  "}]}""";

        Assert.Equal("講者 未知：沒有講者", DiarizedTranscriptFormatter.Format(json));
    }

    [Fact]
    public void Format_ShouldFallBackToText_WhenNoSegments()
    {
        Assert.Equal("整段文字", DiarizedTranscriptFormatter.Format("""{"text":" 整段文字 ","segments":[]}"""));
    }

    [Fact]
    public void Format_ShouldReturnBodyAsIs_WhenNotJson()
    {
        // 寧可沒有講者，也不要整段不見。
        Assert.Equal("不是 JSON 的內容", DiarizedTranscriptFormatter.Format(" 不是 JSON 的內容 "));
        Assert.Equal(string.Empty, DiarizedTranscriptFormatter.Format("  "));
    }

    #endregion

    #region TranscriptSpeakerLabels

    [Fact]
    public void HasLabels_ShouldOnlyMatchLineStartLabels()
    {
        Assert.True(TranscriptSpeakerLabels.HasLabels("講者 A：你好"));
        Assert.True(TranscriptSpeakerLabels.HasLabels("前一段\n\n講者 B：你好"));
        Assert.False(TranscriptSpeakerLabels.HasLabels("今天的講者 A：很精彩"));
        Assert.False(TranscriptSpeakerLabels.HasLabels("沒有代號的舊逐字稿"));
        Assert.False(TranscriptSpeakerLabels.HasLabels(null));
    }

    [Fact]
    public void Strip_ShouldRemoveEveryLineStartLabel()
    {
        Assert.Equal("你好\r\n再見\n\n好", TranscriptSpeakerLabels.Strip("講者 A：你好\r\n講者 B：再見\n\n講者 A：好"));
    }

    [Fact]
    public void EstimateTokens_ShouldIgnoreSpeakerLabels()
    {
        // 代號是我們加的，不是模型的輸出；算進去會讓講者輪替多的段落被誤判成截斷、對半重轉。
        Assert.Equal(
            TranscriptionSegmentPlanner.EstimateTokens("你好再見"),
            TranscriptionSegmentPlanner.EstimateTokens("講者 A：你好\n講者 B：再見"));
    }

    #endregion

    #region AzureOpenAiTranscriptionProvider

    [Theory]
    [InlineData("gpt-4o-transcribe-diarize", true)]
    [InlineData("My-Diarize-Deploy", true)]
    [InlineData("gpt-4o-transcribe", false)]
    [InlineData("gpt-transcribe", false)]
    [InlineData(null, false)]
    public void IsDiarizeModel_ShouldDependOnDeploymentName(string? deployment, bool expected)
    {
        Assert.Equal(expected, AzureOpenAiTranscriptionProvider.IsDiarizeModel(deployment));
    }

    [Fact]
    public async Task Transcribe_DiarizeDeployment_ShouldRequestDiarizedJsonWithChunking_AndFormatSpeakers()
    {
        // 輸入超過 30 秒時 chunking_strategy 是必填，不帶 Azure 直接回 400。
        var factory = new CapturingFactory("""{"text":"你好","segments":[{"speaker":"A","text":"你好"}]}""");
        var provider = CreateProvider(factory, "gpt-4o-transcribe-diarize");

        using var audio = new MemoryStream([1, 2, 3]);
        var text = await provider.TranscribeAsync(audio, "part_0000.mp3", CancellationToken.None);

        Assert.Equal("講者 A：你好", text);
        Assert.Equal("diarized_json", factory.Fields["response_format"]);
        Assert.Equal("auto", factory.Fields["chunking_strategy"]);
    }

    [Fact]
    public async Task Transcribe_PlainDeployment_ShouldKeepTextFormat()
    {
        var factory = new CapturingFactory(" 純文字 ");
        var provider = CreateProvider(factory, "gpt-4o-transcribe");

        using var audio = new MemoryStream([1, 2, 3]);
        var text = await provider.TranscribeAsync(audio, "part_0000.mp3", CancellationToken.None);

        Assert.Equal("純文字", text);
        Assert.Equal("text", factory.Fields["response_format"]);
        Assert.False(factory.Fields.ContainsKey("chunking_strategy"));
    }

    private static AzureOpenAiTranscriptionProvider CreateProvider(IHttpClientFactory factory, string transcriptionModel)
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI", TranscriptionProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://contoso.openai.azure.com",
            ApiKey = "secret-key",
            Model = "gpt-5.6-sol",
            TranscriptionModel = transcriptionModel,
        };

        return new AzureOpenAiTranscriptionProvider(
            factory, Options.Create(settings), NullLogger<AzureOpenAiTranscriptionProvider>.Instance);
    }

    /// <summary>回固定內容，並記下 multipart 裡的文字欄位（檔案欄位略過）。</summary>
    private sealed class CapturingFactory(string body) : IHttpClientFactory
    {
        public Dictionary<string, string> Fields { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, body), disposeHandler: true);

        private sealed class Handler(CapturingFactory owner, string body) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Content is MultipartFormDataContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        var fieldName = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (fieldName is not null && part is StringContent)
                        {
                            owner.Fields[fieldName] = await part.ReadAsStringAsync(cancellationToken);
                        }
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
        }
    }

    #endregion
}

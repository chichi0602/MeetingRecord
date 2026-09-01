using System.Text;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 把逐字稿切成適合單次送進 LLM 的分段（map-reduce 的 map 邊界）。
///
/// 抽成純函式以便單元測試——與 <c>AzureOpenAiTranscriptionProvider.BuildRequestUri</c>、
/// <c>FfmpegMediaConverter.BuildSegmentArguments</c> 同一個慣例。
/// </summary>
public static class TranscriptChunker
{
    /// <summary>
    /// 轉錄產出的逐字稿以此字串接合每個 15 分鐘分段
    /// （見 <c>TranscriptionJobRunner.SegmentSeparator</c>），切段時優先沿用這個邊界。
    /// </summary>
    public const string SegmentSeparator = "\n\n";

    /// <summary>
    /// 單一分段的字元上限。中文約 1 字元 ≈ 1～1.5 token，
    /// 12000 字元換算約 12～18k token，遠低於 gpt-4o 系列的 128k 脈絡長度，
    /// 同時讓每次 map 呼叫維持在可接受的回應時間內。
    /// </summary>
    public const int DefaultMaxChars = 12000;

    /// <summary>
    /// 依 <paramref name="maxChars"/> 把逐字稿切段。
    /// 優先在轉錄的分段邊界切開；單一分段本身就超過上限時才硬切。
    /// 空白內容回傳空集合，內容總和保證與輸入一致（除了被合併掉的分隔字串）。
    /// </summary>
    public static IReadOnlyList<string> Split(string? transcript, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var segment in transcript.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var piece in SplitOversizedSegment(segment, maxChars))
            {
                // 併入目前這塊會超過上限，就先收掉、另起一塊。
                var lengthIfAppended = current.Length == 0
                    ? piece.Length
                    : current.Length + SegmentSeparator.Length + piece.Length;

                if (current.Length > 0 && lengthIfAppended > maxChars)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(SegmentSeparator);
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// 單一分段就超過上限時硬切成數塊；未超過則原樣回傳。
    /// 硬切會切在句子中間，但這是最後手段——正常的轉錄分段遠小於上限。
    /// </summary>
    private static IEnumerable<string> SplitOversizedSegment(string segment, int maxChars)
    {
        if (segment.Length <= maxChars)
        {
            yield return segment;
            yield break;
        }

        for (var offset = 0; offset < segment.Length; offset += maxChars)
        {
            yield return segment.Substring(offset, Math.Min(maxChars, segment.Length - offset));
        }
    }
}

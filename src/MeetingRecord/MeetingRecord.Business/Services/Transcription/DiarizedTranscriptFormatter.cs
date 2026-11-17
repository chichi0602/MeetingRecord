using System.Text;
using System.Text.Json;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>diarized_json 裡的一句話。時間（秒）是相對於送出去的那個音檔。</summary>
public sealed record DiarizedSegment(string Speaker, string Text, double? Start, double? End)
{
    public double? Seconds => Start is { } start && End is { } end && end > start ? end - start : null;
}

/// <summary>
/// 解析後的 diarized_json。<see cref="FallbackText"/> 是拿不到 segments 時的退路
/// （JSON 的 <c>text</c>，或連 JSON 都不是時的原字串）。
/// </summary>
public sealed record DiarizedTranscription(IReadOnlyList<DiarizedSegment> Segments, string FallbackText)
{
    public string ToText() => DiarizedTranscriptFormatter.Format(Segments, FallbackText);
}

/// <summary>
/// 把 gpt-4o-transcribe-diarize 的 <c>diarized_json</c> 回應轉成逐字稿文字（1.1.63）。
/// 抽成純函式以便單元測試——與 <see cref="TranscriptionSegmentPlanner"/> 同一個慣例。
///
/// <para>
/// 輸出一行一位講者（<c>講者 A：……</c>），相鄰同一講者合併成一行。
/// ⚠️ 行與行之間用 <c>\n</c>，<b>不能用 <c>\n\n</c></b>：<c>\n\n</c> 是轉錄分段的邊界，
/// <c>TranscriptChunker</c> 與 <see cref="TranscriptionNoiseFilter"/> 都靠它切段。
/// </para>
///
/// <para>
/// 解析不出講者時退回 JSON 的 <c>text</c>，連 <c>text</c> 都沒有就回原字串——寧可沒有講者，也不要整段不見。
/// 1.1.64 拆成 <see cref="Parse"/> 與 <see cref="Format(IReadOnlyList{DiarizedSegment}, string)"/>：
/// 整場對齊要先拿到每句的時間剪聲音參考、改代號，再轉成文字。
/// </para>
/// </summary>
public static class DiarizedTranscriptFormatter
{
    public static string Format(string? body) => Parse(body).ToText();

    public static DiarizedTranscription Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new DiarizedTranscription([], string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DiarizedTranscription([], body.Trim());
            }

            var segments = new List<DiarizedSegment>();
            if (root.TryGetProperty("segments", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var text = ReadString(item, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var speaker = ReadString(item, "speaker");
                    segments.Add(new DiarizedSegment(
                        string.IsNullOrEmpty(speaker) ? TranscriptSpeakerLabels.UnknownSpeaker : speaker,
                        text,
                        ReadNumber(item, "start"),
                        ReadNumber(item, "end")));
                }
            }

            return new DiarizedTranscription(segments, ReadString(root, "text") ?? body.Trim());
        }
        catch (JsonException)
        {
            return new DiarizedTranscription([], body.Trim());
        }
    }

    public static string Format(IReadOnlyList<DiarizedSegment> segments, string fallbackText)
    {
        var lines = new List<(string Speaker, StringBuilder Text)>();
        foreach (var segment in segments)
        {
            if (lines.Count > 0 && lines[^1].Speaker == segment.Speaker)
            {
                AppendText(lines[^1].Text, segment.Text);
            }
            else
            {
                lines.Add((segment.Speaker, new StringBuilder(segment.Text)));
            }
        }

        return lines.Count > 0
            ? string.Join("\n", lines.Select(x => TranscriptSpeakerLabels.Format(x.Speaker) + x.Text))
            : fallbackText;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>中文直接接上；兩邊都是英數字時補一個空白，英文單字才不會黏在一起。</summary>
    private static void AppendText(StringBuilder target, string text)
    {
        if (target.Length > 0 && char.IsAsciiLetterOrDigit(target[^1]) && char.IsAsciiLetterOrDigit(text[0]))
        {
            target.Append(' ');
        }

        target.Append(text);
    }
}

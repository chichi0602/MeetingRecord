using System.Text;
using System.Text.Json;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Transcription;

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
/// </para>
/// </summary>
public static class DiarizedTranscriptFormatter
{
    public static string Format(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return body.Trim();
            }

            var lines = new List<(string Speaker, StringBuilder Text)>();
            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segments.EnumerateArray())
                {
                    var text = ReadString(segment, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var speaker = ReadString(segment, "speaker");
                    if (string.IsNullOrEmpty(speaker))
                    {
                        speaker = TranscriptSpeakerLabels.UnknownSpeaker;
                    }

                    if (lines.Count > 0 && lines[^1].Speaker == speaker)
                    {
                        AppendText(lines[^1].Text, text);
                    }
                    else
                    {
                        lines.Add((speaker, new StringBuilder(text)));
                    }
                }
            }

            if (lines.Count > 0)
            {
                return string.Join("\n", lines.Select(x => TranscriptSpeakerLabels.Format(x.Speaker) + x.Text));
            }

            return ReadString(root, "text") ?? body.Trim();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
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

using System.Globalization;
using System.Text.RegularExpressions;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>FFmpeg <c>silencedetect</c> 偵測到的一段安靜（秒）。</summary>
public readonly record struct SilenceSpan(double Start, double End)
{
    public double Duration => End - Start;

    public double Middle => (Start + End) / 2;
}

/// <summary>
/// 決定音檔要切在哪裡（1.1.61）。抽成純函式以便單元測試——與 <see cref="MediaDurationParser"/> 同一個慣例，
/// 不碰 IO、不碰行程。
///
/// <para>
/// 以前固定每 5 分鐘切一刀，常切在一句話、甚至一個字的中間，交界處會少字或聽錯。
/// 現在先用 FFmpeg 的 silencedetect 找出停頓，每段在「上限前 60 秒內」挑最長的停頓切；
/// 找不到停頓才退回整整 5 分鐘。⚠️ 段長只會<b>比上限短、不會比上限長</b>——上限是轉錄模型的輸出長度
/// （見 <c>MeetingMediaPolicy.TranscriptionSegmentSeconds</c>），超過就會被截斷。
/// </para>
/// </summary>
public static partial class TranscriptionSegmentPlanner
{
    /// <summary>每段在上限前多少秒內找停頓。60 秒：段長落在 4～5 分鐘，段數最多多出約四分之一。</summary>
    public const int SearchWindowSeconds = 60;

    /// <summary>
    /// silencedetect 的參數：低於 -30dB 且持續 0.4 秒以上算停頓。
    /// 會議錄音常有冷氣、鍵盤聲，門檻訂太低（例如 -50dB）會一個停頓都抓不到，只是退回固定切點、不會壞。
    /// </summary>
    public const string SilenceDetectFilter = "silencedetect=noise=-30dB:d=0.4";

    /// <summary>
    /// 轉錄模型單次輸出的上限（token）。gpt-4o-transcribe 約 2000，超過的部分會被靜默截掉、API 不回錯誤。
    /// </summary>
    public const int TranscriptionOutputTokenLimit = 2000;

    /// <summary>
    /// 估算超過這個 token 數就視為「可能被截斷」，該段對半重轉。
    /// 實測被截斷的段落停在 1800～2400 字；5 分鐘講話最密約 1500 字，取 1700 兩邊都留了餘裕。
    /// </summary>
    public const int TruncationSuspectTokens = 1700;

    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceStartPattern();

    [GeneratedRegex(@"silence_end:\s*(-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEndPattern();

    /// <summary>
    /// 從 FFmpeg 的 stderr 取出所有停頓。只有開始、沒有結束的（音檔結尾的安靜）略過——切在那裡沒有意義。
    /// </summary>
    public static IReadOnlyList<SilenceSpan> ParseSilences(string? ffmpegOutput)
    {
        if (string.IsNullOrWhiteSpace(ffmpegOutput))
        {
            return [];
        }

        var result = new List<SilenceSpan>();
        double? pendingStart = null;

        foreach (var line in ffmpegOutput.Split('\n'))
        {
            var start = SilenceStartPattern().Match(line);
            if (start.Success)
            {
                pendingStart = Math.Max(0, double.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var end = SilenceEndPattern().Match(line);
            if (end.Success && pendingStart is { } begin)
            {
                var finish = double.Parse(end.Groups[1].Value, CultureInfo.InvariantCulture);
                if (finish > begin)
                {
                    result.Add(new SilenceSpan(begin, finish));
                }

                pendingStart = null;
            }
        }

        return result;
    }

    /// <summary>
    /// 決定切點（秒，由小到大）。音檔不超過 <paramref name="maxSegmentSeconds"/> 時回空清單（不切）。
    /// </summary>
    public static IReadOnlyList<double> PlanCutPoints(
        double totalSeconds,
        IReadOnlyList<SilenceSpan> silences,
        int maxSegmentSeconds,
        int searchWindowSeconds = SearchWindowSeconds)
    {
        var cuts = new List<double>();
        if (totalSeconds <= 0 || maxSegmentSeconds <= 0)
        {
            return cuts;
        }

        var window = Math.Clamp(searchWindowSeconds, 0, maxSegmentSeconds - 1);
        var previous = 0d;

        while (totalSeconds - previous > maxSegmentSeconds)
        {
            var latest = previous + maxSegmentSeconds;
            var earliest = latest - window;

            // 視窗內最長的停頓；一樣長時取比較晚的（段落盡量長、段數少）。
            var best = silences
                .Where(x => x.Middle >= earliest && x.Middle <= latest)
                .OrderByDescending(x => x.Duration)
                .ThenByDescending(x => x.Middle)
                .Select(x => (double?)x.Middle)
                .FirstOrDefault();

            var cut = Math.Round(best ?? latest, 3);
            cuts.Add(cut);
            previous = cut;
        }

        return cuts;
    }

    /// <summary>
    /// 對半切一段時的切點：中間 30% 範圍內（35%～65%）最長的停頓；沒有就切正中間。
    /// </summary>
    public static double PickSplitPoint(double totalSeconds, IReadOnlyList<SilenceSpan> silences)
    {
        var middle = totalSeconds / 2;
        var earliest = totalSeconds * 0.35;
        var latest = totalSeconds * 0.65;

        var best = silences
            .Where(x => x.Middle >= earliest && x.Middle <= latest)
            .OrderByDescending(x => x.Duration)
            .ThenBy(x => Math.Abs(x.Middle - middle))
            .Select(x => (double?)x.Middle)
            .FirstOrDefault();

        return Math.Round(best ?? middle, 3);
    }

    /// <summary>依切點算出每一段的秒數，最後一段拿剩下的。</summary>
    public static IReadOnlyList<double> SegmentDurations(double totalSeconds, IReadOnlyList<double> cutPoints)
    {
        var result = new List<double>(cutPoints.Count + 1);
        var previous = 0d;

        // 取到毫秒：浮點相減會出現 249.16000000000008 這種尾巴，直接進帳本很難看。
        foreach (var cut in cutPoints)
        {
            result.Add(Math.Round(cut - previous, 3));
            previous = cut;
        }

        result.Add(Math.Round(Math.Max(totalSeconds - previous, 0), 3));
        return result;
    }

    /// <summary>
    /// 估算轉錄結果的 token 數：中日韓文字一字約一個 token，其他非空白字元約四個字一個 token。
    /// 只用來判斷「是不是頂到輸出上限」，不用於計費。
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        return cjk + (other + 3) / 4;
    }

    /// <summary>這段轉錄結果長到可能是被輸出上限截斷的。</summary>
    public static bool LooksTruncated(string? text) => EstimateTokens(text) >= TruncationSuspectTokens;

    private static bool IsCjk(char ch) =>
        ch is >= '㐀' and <= '鿿'      // 中日韓統一表意文字（含擴充 A）
            or >= '豈' and <= '﫿'     // 相容表意文字
            or >= '　' and <= '〿'     // 中文標點
            or >= '＀' and <= '￯';    // 全形字元
}

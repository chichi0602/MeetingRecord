namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 一場轉錄的講者對齊狀態（1.1.64）。純邏輯，剪檔與送 API 由 <see cref="TranscriptionJobRunner"/> 做。
///
/// <para>
/// diarize 模型每次呼叫都從 A 重新編號，5 分鐘一段時前後段的「講者 A」不一定是同一人。
/// 做法：某段出現還沒有參考的講者時，剪他最長的一句當聲音參考、配發編號 1～4；
/// 後面各段都帶上這些參考，認得的講者就回傳同一個編號。
/// </para>
///
/// <para>
/// ⚠️ 編號刻意用數字：API 對認不得的講者回 A、B…，參考名稱若也用字母會撞名。
/// 滿 4 位（API 上限）之後的新講者維持字母，只在該段有效。
/// </para>
/// </summary>
public sealed class SpeakerAlignmentSession
{
    /// <summary>API 一次最多接受的聲音參考數。</summary>
    public const int MaxReferences = 4;

    /// <summary>聲音參考的長度限制（API 規定 2～10 秒）。</summary>
    public const double MinClipSeconds = 2;

    public const double MaxClipSeconds = 10;

    private readonly List<SpeakerReference> references = [];

    public IReadOnlyList<SpeakerReference> References => references;

    /// <summary>供應商不接受聲音參考（例如回 400）後停用，這場之後照舊每段各自標字母。</summary>
    public bool IsDisabled { get; private set; }

    public void Disable() => IsDisabled = true;

    /// <summary>
    /// 挑出這段裡還沒有參考、需要剪聲音的講者：每位取他最長的一句（至少 2 秒，最多取 10 秒），
    /// 依第一次開口的順序，最多補到 <see cref="MaxReferences"/> 位。
    /// </summary>
    public IReadOnlyList<(string Speaker, double Start, double Seconds)> PlanNewReferences(IReadOnlyList<DiarizedSegment> segments)
    {
        var slots = MaxReferences - references.Count;
        if (IsDisabled || slots <= 0)
        {
            return [];
        }

        var known = references.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        return segments
            .Where(x => !known.Contains(x.Speaker)
                && x.Speaker != Share.Helpers.TranscriptSpeakerLabels.UnknownSpeaker
                && x.Seconds >= MinClipSeconds)
            .GroupBy(x => x.Speaker)
            .Select(group => group.OrderByDescending(x => x.Seconds).First())
            .OrderBy(x => segments.First(s => s.Speaker == x.Speaker).Start)
            .Take(slots)
            .Select(x => (x.Speaker, x.Start!.Value, Math.Min(x.Seconds!.Value, MaxClipSeconds)))
            .ToList();
    }

    /// <summary>登記一份聲音參考，回傳配發的編號（"1"～"4"）。</summary>
    public string Register(string dataUrl)
    {
        if (references.Count >= MaxReferences)
        {
            throw new InvalidOperationException($"聲音參考最多 {MaxReferences} 份。");
        }

        var name = (references.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        references.Add(new SpeakerReference(name, dataUrl));
        return name;
    }

    /// <summary>把這段裡剛登記參考的字母講者改成配發的編號。</summary>
    public static IReadOnlyList<DiarizedSegment> Relabel(
        IReadOnlyList<DiarizedSegment> segments,
        IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return segments;
        }

        return segments
            .Select(x => renames.TryGetValue(x.Speaker, out var name) ? x with { Speaker = name } : x)
            .ToList();
    }
}

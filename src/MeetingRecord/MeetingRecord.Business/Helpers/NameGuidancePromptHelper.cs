using System.Text;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 組出「專有名詞與人名對照」的提示詞區塊，附加在送給模型的內容前面。
///
/// <para>
/// 逐字稿由語音辨識產生，人名與專有名詞常被聽成同音字。專案上維護的
/// <c>Project.GlossaryTerms</c>（常用名詞）與本次勾選的與會者，會在這裡組成一段指示，
/// 讓模型把明顯是誤認的寫法改回正確名稱。
/// </para>
///
/// <para>
/// ⚠️ <b>刻意不走 <c>{{}}</c> 佔位符。</b>5 個內建範本與所有使用者自訂範本都沒有寫佔位符，
/// 只加變數的話這個功能會完全不生效，<b>而且不會報錯</b>——未知變數在本專案是「原樣保留」。
/// </para>
///
/// <para>
/// ⚠️ <b>兩份清單都空時必須回傳 <see cref="string.Empty"/>，連換行都不能有。</b>
/// <c>MeetingDraftJobRunnerTests</c> 對 UserPrompt 是全等比對，多一個字元就紅；
/// 這條同時也是「沒設名單時行為與先前逐字元相同」的回歸保證。
/// </para>
///
/// 抽成純函式以便單元測試（本專案的既有慣例）。
/// </summary>
public static class NameGuidancePromptHelper
{
    /// <summary>
    /// reduce 階段結尾的極短提醒，用來對沖長提示詞的中段注意力衰減。
    /// map 階段不用——那邊的內容短，而且每段都會重複一次並不划算。
    /// </summary>
    public const string ReduceReminder = "\n\n（再次提醒：人名與專有名詞請依開頭名單的正確寫法。）";

    private const string Heading = "【本次會議的專有名詞與人名對照】";

    /// <summary>
    /// 組出要附加在提示詞前面的名單區塊。兩份清單都沒有有效值時回傳空字串。
    /// </summary>
    /// <param name="glossaryTerms">專案的常用名詞（術語、產品名）。</param>
    /// <param name="attendees">本次會議勾選的與會人員。</param>
    public static string Build(IReadOnlyList<string>? glossaryTerms, IReadOnlyList<string>? attendees)
    {
        var terms = Clean(glossaryTerms);
        var people = Clean(attendees);

        if (terms.Count == 0 && people.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(Heading);
        builder.AppendLine("逐字稿由語音辨識產生，專有名詞與人名常出現同音或近音的誤字。");

        // ⚠️ 這句是關鍵：共用的 SystemPrompt 寫著「不要臆測或補充逐字稿沒有提到的資訊」，
        // 與「把聽錯的名字改掉」直接衝突。把清單重新定義成「已提供的資訊」才化解得掉，
        // 而且要跟授權它的資料放在一起，不要去改那個共用常數（那會動到每一次呼叫）。
        builder.AppendLine("下列名單屬於「已提供的資訊」，依此更正用字不屬於臆測。");
        builder.AppendLine();

        if (terms.Count > 0)
        {
            builder.AppendLine($"常用名詞：{string.Join("、", terms)}");
        }

        if (people.Count > 0)
        {
            builder.AppendLine($"與會人員：{string.Join("、", people)}");
        }

        builder.AppendLine();
        builder.AppendLine("處理規則：");

        var rule = 1;
        builder.AppendLine($"{rule++}. 逐字稿中若出現與上列名稱同音或音近、明顯是辨識錯誤的寫法，改為上列的正確寫法。");

        if (people.Count > 0)
        {
            builder.AppendLine($"{rule++}. 發言者標示只能使用「與會人員」名單中的名字。");
        }

        builder.AppendLine($"{rule++}. 判斷不出對應哪一個名稱時，保留逐字稿原本的寫法，不要猜。");

        // 把「不要臆測」的守備範圍收回來：授權的只有「改寫已存在的誤字」，不是「無中生有」。
        builder.AppendLine(
            $"{rule}. 不得新增名單中沒有、逐字稿也沒提到的人名或名詞；名單中未實際發言的人，不要憑空替他生出發言或待辦。");

        builder.AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// 去空白、捨棄空項、忽略大小寫去重並保留原順序。
    /// 與 <see cref="TagStringHelper.ToStored"/> 同一套規則——呼叫端多半已經過那裡，
    /// 但這裡不能假設（單元測試與日後的呼叫端都可能直接傳原始清單）。
    /// </summary>
    private static List<string> Clean(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>(values.Count);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                cleaned.Add(trimmed);
            }
        }

        return cleaned;
    }
}

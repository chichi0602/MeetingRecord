using System.Text;

namespace MeetingRecord.Web.Export;

/// <summary>CSV 的一個欄位：表頭與取值方式。</summary>
public sealed record CsvColumn<T>(string Header, Func<T, string?> Value);

/// <summary>
/// 匯出 CSV（0.4.122，系統例外紀錄用；比照 NET10-Blazor-Starter 的 <c>TabularExport.ToCsv</c>，只做 CSV）。
///
/// <para>⚠️ 一律 UTF-8 含 BOM：沒有 BOM 時 Excel 會用系統編碼開檔，中文全變亂碼。</para>
/// <para>⚠️ 以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c> 開頭的儲存格前面補一個單引號，避免 Excel 把它當公式執行（CSV injection）——
/// 例外訊息與頁面路徑都可能含使用者輸入。</para>
/// </summary>
public static class CsvExport
{
    public const string ContentType = "text/csv";

    public static string ToCsvText<T>(IReadOnlyList<CsvColumn<T>> columns, IEnumerable<T> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', columns.Select(x => Quote(x.Header)))).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(',', columns.Select(x => Quote(Neutralize(x.Value(row)))))).Append("\r\n");
        }

        return builder.ToString();
    }

    public static byte[] ToCsv<T>(IReadOnlyList<CsvColumn<T>> columns, IEnumerable<T> rows)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(ToCsvText(columns, rows));
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>含逗號、雙引號或換行就用雙引號包起來，內部雙引號加倍（RFC 4180）。</summary>
    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string? Neutralize(string? value)
        => value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
}

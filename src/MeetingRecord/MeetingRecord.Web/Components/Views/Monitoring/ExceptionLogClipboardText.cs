using System.Text;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Web.Components.Views.Monitoring
{
    /// <summary>
    /// 系統例外紀錄「點列複製」與「複製目前查詢結果」的剪貼簿文字。純函式，無 I/O、無 DI。
    ///
    /// 欄位與明細窗一致，<b>含</b>帳號與 UserId —— 這份文字是管理員自己貼給開發人員的；
    /// 要送給外部 AI 的版本在 <see cref="MeetingRecord.Web.Diagnostics.AiExceptionPromptBuilder"/>，那邊刻意去識別化，兩者不可混用。
    /// </summary>
    public static class ExceptionLogClipboardText
    {
        private const string StackStartMarker = "----- 完整堆疊開始 -----";
        private const string StackEndMarker = "----- 完整堆疊結束 -----";
        private const string MissingStackText = "完整堆疊：堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。";
        private static readonly string RecordSeparator = "\n" + new string('=', 50) + "\n\n";

        /// <summary>明細窗與複製文字共用的「使用者」顯示：只有帳號與 UserId，不含姓名／Email。</summary>
        public static string FormatAccount(ExceptionLogAdapterModel item)
        {
            var account = string.IsNullOrWhiteSpace(item.Account) ? "—" : item.Account;
            return item.UserId is null ? account : $"{account}（UserId={item.UserId}）";
        }

        /// <summary>
        /// 單筆：明細欄位＋完整堆疊。
        ///
        /// ⚠️ 一律用 '\n' 而不是 AppendLine：堆疊檔在 Windows 上是 CRLF，混用兩種行尾貼到工單會很難看。
        /// 堆疊用文字標記包起來而不是 Markdown 程式碼區塊：堆疊內容本身可能含有反引號。
        /// </summary>
        public static string Build(ExceptionLogAdapterModel item, string? stackTrace)
        {
            ArgumentNullException.ThrowIfNull(item);

            var builder = new StringBuilder();
            AppendField(builder, "例外類型", item.ExceptionType);
            AppendField(builder, "訊息", item.Message);
            AppendField(builder, "來源", item.Source);
            AppendField(builder, "頁面", item.Page ?? "—");
            AppendField(builder, "操作（日誌訊息樣板）", item.Operation ?? "—");
            AppendField(builder, "記錄器", item.LoggerName ?? "—");
            AppendField(builder, "使用者", FormatAccount(item));
            AppendField(builder, "累計次數", item.OccurrenceCount.ToString("N0"));
            AppendField(builder, "首次發生", item.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendField(builder, "最後發生", item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendField(builder, "最後追蹤碼", item.LastTraceId ?? "—");

            builder.Append('\n');
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                builder.Append(MissingStackText).Append('\n');
            }
            else
            {
                builder.Append(StackStartMarker).Append('\n')
                    .Append(Normalize(stackTrace).TrimEnd('\n')).Append('\n')
                    .Append(StackEndMarker).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>多筆：每筆與 <see cref="Build"/> 完全相同，筆與筆之間以一條分隔線隔開。</summary>
        public static string BuildMany(IEnumerable<(ExceptionLogAdapterModel Item, string? StackTrace)> rows)
            => string.Join(RecordSeparator, rows.Select(row => Build(row.Item, row.StackTrace)));

        private static void AppendField(StringBuilder builder, string label, string value)
            => builder.Append(label).Append('：').Append(Normalize(value)).Append('\n');

        private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}

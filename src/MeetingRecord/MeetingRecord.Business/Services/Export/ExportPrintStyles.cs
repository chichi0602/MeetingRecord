namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 匯出 PDF 共用的列印樣式。
///
/// 字型堆疊把 Windows 與 macOS 的常見中文字型都列上，交給瀏覽器挑——
/// 這正是不必把 CJK 字型檔嵌進專案的原因（無頭瀏覽器會自行做子集嵌入）。
///
/// <see cref="Base"/> 只管頁面層級的事（紙張、字型、內文元素、分頁規則）。
/// 各種文件的版面樣式（會議紀錄的表頭、AI 問答的發話者標籤）由各自的
/// exporter 再串一段上去，不要互相套用。
/// </summary>
internal static class ExportPrintStyles
{
    /// <summary>頁面設定與 Markdown 內文元素的樣式。內文一律包在 <c>.doc-body</c> 裡。</summary>
    public const string Base = """
        @page { size: A4; margin: 18mm 16mm; }
        body {
            margin: 0;
            font-family: "Microsoft JhengHei", "PingFang TC", "Noto Sans TC", "Hiragino Sans", sans-serif;
            font-size: 11pt;
            line-height: 1.7;
            color: #1f2d3d;
        }
        .doc-title { font-size: 18pt; margin: 0 0 12px; }
        .doc-meta { margin: 0; font-size: 10pt; color: #4a5a6b; }
        .doc-meta-row { display: flex; gap: 8px; margin: 2px 0; }
        .doc-meta dt { flex: none; min-width: 5em; font-weight: 600; }
        .doc-meta dd { margin: 0; }
        .doc-rule { margin: 14px 0 18px; border: none; border-top: 1px solid #c8d1da; }
        .doc-body h1 { font-size: 16pt; }
        .doc-body h2 { font-size: 14pt; margin: 18px 0 8px; }
        .doc-body h3 { font-size: 12pt; margin: 14px 0 6px; }
        .doc-body p { margin: 8px 0; }
        .doc-body ul, .doc-body ol { margin: 8px 0; padding-left: 1.6em; }
        .doc-body li { margin: 3px 0; }
        .doc-body pre {
            background: #f5f7fa;
            padding: 10px 12px;
            border-radius: 4px;
            white-space: pre-wrap;
            word-break: break-word;
        }
        .doc-body code { font-family: Consolas, "Courier New", monospace; font-size: 10pt; }
        .doc-body blockquote {
            margin: 8px 0;
            padding-left: 12px;
            border-left: 3px solid #c8d1da;
            color: #4a5a6b;
        }
        .doc-body table {
            width: 100%;
            border-collapse: collapse;
            margin: 10px 0;
            font-size: 10pt;
            table-layout: fixed;
        }
        .doc-body th, .doc-body td {
            border: 1px solid #c8d1da;
            padding: 5px 8px;
            text-align: left;
            vertical-align: top;
            word-break: break-word;
        }
        .doc-body th { background: #f0f3f7; font-weight: 600; }
        /* 標題不要落在頁尾、表格列不要被切成兩半。 */
        .doc-body h1, .doc-body h2, .doc-body h3 { break-after: avoid; }
        .doc-body tr, .doc-body li { break-inside: avoid; }
        """;
}

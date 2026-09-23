using System.Text;

namespace MeetingRecord.Tests;

/// <summary>
/// 掃描所有 <c>.razor</c> 裡的 <c>&lt;Modal&gt;</c> 標籤，守住 0.4.82 的對話框版面規則。
///
/// <para>
/// 0.4.69 的自我檢討是「100% 的視覺改動，單元測試完全證明不了」——留白、捲動、分欄
/// 確實證明不了。但**結構**證明得了：一個 Modal 有沒有掛尺寸 class、有沒有留下已經失效的
/// <c>Width</c>，都是原始檔上的事實。0.4.69 之所以留下 16 個死 class 與 13 個沒有 class 的
/// Modal，正是因為當時沒有任何東西在看著。
/// </para>
///
/// <para>
/// 設計比照 <c>MenuIconTests</c>：從測試組件往回走到原始碼目錄，直接讀檔。
/// </para>
/// </summary>
public sealed class ModalSizeClassTests
{
    /// <summary>
    /// 尺寸分級。規則全部在 <c>Components/Commons/FormModalHelper.razor</c> 的
    /// 全域 <c>&lt;style&gt;</c> 裡，這份清單要跟著它一起改。
    /// </summary>
    private static readonly HashSet<string> SizeClasses =
    [
        "form-modal-compact",
        "form-modal-standard",
        "form-modal-large",
        "meeting-view-modal",
    ];

    /// <summary>
    /// 沒有掛進任何路由、也沒有被任何元件引用的示範檔（<c>RoleViewViewSample</c>）。
    /// 它是先前就存在的死程式碼，不在本次範圍內，但留著會讓下面兩條規則永遠是紅的。
    /// </summary>
    private static readonly HashSet<string> ExcludedFiles =
    [
        "RoleViewViewSample.razor",
    ];

    [Fact]
    public void EveryModal_ShouldDeclareExactlyOneSizeClass()
    {
        var offenders = new List<string>();

        foreach (var modal in EnumerateModals())
        {
            // Class="@Xxx" 是執行期才決定的，靜態掃描判斷不了。
            // 目前只有 MarkdownEditorModal（依 CanEdit 在兩級之間切換）走這條路。
            if (modal.ClassValue is null || modal.ClassValue.StartsWith('@'))
            {
                if (modal.ClassValue is null)
                {
                    offenders.Add($"{modal.Location}：完全沒有 Class");
                }

                continue;
            }

            var declared = modal.ClassValue
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(SizeClasses.Contains);

            if (declared != 1)
            {
                offenders.Add($"{modal.Location}：尺寸 class 有 {declared} 個（Class=\"{modal.ClassValue}\"）");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "每個 Modal 都要剛好掛一個尺寸 class（見 FormModalHelper.razor）：\n"
                + string.Join('\n', offenders));
    }

    [Fact]
    public void ModalWithSizeClass_ShouldNotAlsoSetWidth()
    {
        var offenders = EnumerateModals()
            .Where(m => m.HasWidth)
            .Select(m => $"{m.Location}：同時有 Class 與 Width")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            // 尺寸 class 的 width 是 author-important，依 CSS 串聯順序贏過 Width 產生的
            // inline style。兩者並存時 Width 是死參數，留著只會讓下一個人以為調它有用。
            "掛了尺寸 class 就不要再設 Width，它不會生效：\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Scanner_ShouldActuallyFindTheModals()
    {
        // ⚠️ 沒有這一條的話，上面兩支在「掃描器壞掉、一個 Modal 都沒找到」時會全綠——
        // 那是最糟的失敗模式：測試看起來在守，其實什麼都沒守。
        var modals = EnumerateModals().ToList();

        Assert.True(modals.Count >= 15, $"只掃到 {modals.Count} 個 Modal，掃描器可能壞了。");
        Assert.Contains(modals, m => m.Location.Contains("MarkdownEditorModal.razor"));
    }

    private static IEnumerable<ModalTag> EnumerateModals()
    {
        var componentsRoot = ResolveComponentsRoot();

        foreach (var file in Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (ExcludedFiles.Contains(fileName))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var from = 0;

            while (true)
            {
                var at = text.IndexOf("<Modal ", from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                var tag = ReadTag(text, at);
                var line = text.Take(at).Count(c => c == '\n') + 1;

                yield return new ModalTag(
                    $"{fileName}:{line}",
                    ReadAttribute(tag, "Class"),
                    ReadAttribute(tag, "Width") is not null);

                from = at + tag.Length;
            }
        }
    }

    /// <summary>
    /// 從 <c>&lt;Modal</c> 讀到標籤結束的 <c>&gt;</c>。
    ///
    /// <para>
    /// ⚠️ 必須追蹤引號狀態，不能直接找第一個 <c>&gt;</c>：屬性值裡的 lambda
    /// （<c>OnClick="@(() =&gt; ...)"</c>）帶著一個 <c>&gt;</c>，直接找會在標籤中間就斷掉。
    /// 今天的 Modal 標籤上剛好沒有 lambda，但這種掃描器壞掉時是靜靜地漏掉，不是報錯。
    /// </para>
    /// </summary>
    private static string ReadTag(string text, int start)
    {
        var builder = new StringBuilder();
        var inQuote = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            builder.Append(c);

            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == '>' && !inQuote)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string? ReadAttribute(string tag, string name)
    {
        var marker = $"{name}=\"";
        var at = tag.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var valueStart = at + marker.Length;
        var valueEnd = tag.IndexOf('"', valueStart);

        return valueEnd < 0 ? null : tag[valueStart..valueEnd];
    }

    private static string ResolveComponentsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MeetingRecord.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.Web/Components。");
    }

    private sealed record ModalTag(string Location, string? ClassValue, bool HasWidth);
}

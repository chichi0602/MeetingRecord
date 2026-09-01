namespace MeetingRecord.Web.Components.Layout;

/// <summary>側邊欄的一個區塊。</summary>
public sealed record SidebarMenuSection(string Name, IReadOnlyList<SidebarMenuEntry> Entries);

/// <summary>
/// 區塊中的一個項目。<see cref="Index"/> 是該項在<b>完整</b>選單清單中的原始索引，
/// 不是它在區塊內的位置。
/// </summary>
public sealed record SidebarMenuEntry(int Index, SidebarMenuItemModel Item);

/// <summary>
/// 把頂層選單項目依 <c>Section</c> 分組。抽成純函式以便單元測試——
/// 與 <c>TranscriptChunker.Split</c>、<c>AzureOpenAiTextGenerationProvider.BuildRequestUri</c> 同一個慣例。
/// </summary>
public static class SidebarMenuSectionBuilder
{
    /// <summary>預設的區塊標題，給 <c>Menu.json</c> 未標註 section 的節點使用。</summary>
    public const string DefaultSectionName = "功能選單";

    /// <summary>
    /// 依 <c>Section</c> 分組，保留區塊的首次出現順序，區塊內也保留原始順序。
    ///
    /// <b>每個項目都帶著它在完整清單中的原始索引</b>：選單的選取狀態鍵值是
    /// <c>root-{index}</c>，必須與 <c>NavMenu.TryFindActiveMenuPath</c> 對完整清單
    /// 遞迴算出來的結果一致。若分組後各區塊各自從 0 重新編號，
    /// 高亮與自動展開會全部錯位。
    /// </summary>
    public static IReadOnlyList<SidebarMenuSection> Build(IReadOnlyList<SidebarMenuItemModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = new List<string>();
        var grouped = new Dictionary<string, List<SidebarMenuEntry>>(StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var sectionName = string.IsNullOrWhiteSpace(item.Section)
                ? DefaultSectionName
                : item.Section.Trim();

            if (!grouped.TryGetValue(sectionName, out var entries))
            {
                entries = [];
                grouped[sectionName] = entries;
                ordered.Add(sectionName);
            }

            entries.Add(new SidebarMenuEntry(index, item));
        }

        return [.. ordered.Select(name => new SidebarMenuSection(name, grouped[name]))];
    }
}

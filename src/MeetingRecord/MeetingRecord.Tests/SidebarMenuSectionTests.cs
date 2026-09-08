using MeetingRecord.Web.Components.Layout;

namespace MeetingRecord.Tests;

/// <summary>
/// 側邊欄分區的單元測試。這裡守的是兩件「壞掉也不會有錯誤訊息」的事：
/// 分組後原始索引是否保留（影響選取高亮），以及 Section 是否在 Clone 時被保留。
/// </summary>
public sealed class SidebarMenuSectionTests
{
    #region 分組與順序

    [Fact]
    public void Build_ShouldGroupBySectionPreservingFirstAppearanceOrder()
    {
        var items = new List<SidebarMenuItemModel>
        {
            new() { Id = 61, Name = "會議紀錄", Section = "核心功能" },
            new() { Id = 21, Name = "專案項目", Section = "核心功能" },
            new() { Id = 3, Name = "系統管理", Section = "功能選單" },
            new() { Id = 4, Name = "登出", Section = "功能選單" },
        };

        var sections = SidebarMenuSectionBuilder.Build(items);

        Assert.Equal(2, sections.Count);
        Assert.Equal("核心功能", sections[0].Name);
        Assert.Equal("功能選單", sections[1].Name);
        Assert.Equal(["會議紀錄", "專案項目"], sections[0].Entries.Select(x => x.Item.Name));
        Assert.Equal(["系統管理", "登出"], sections[1].Entries.Select(x => x.Item.Name));
    }

    [Fact]
    public void Build_ShouldPreserveOriginalIndex()
    {
        // 選取狀態的鍵值是 root-{index}，index 必須是「在完整清單中的位置」，
        // 不是「在區塊內的位置」。分組後各自從 0 重編號會讓高亮與自動展開全部錯位。
        var items = new List<SidebarMenuItemModel>
        {
            new() { Id = 61, Name = "會議紀錄", Section = "核心功能" },
            new() { Id = 3, Name = "系統管理", Section = "功能選單" },
            new() { Id = 21, Name = "專案項目", Section = "核心功能" },
            new() { Id = 4, Name = "登出", Section = "功能選單" },
        };

        var sections = SidebarMenuSectionBuilder.Build(items);

        var core = sections.Single(x => x.Name == "核心功能");
        var main = sections.Single(x => x.Name == "功能選單");

        Assert.Equal([0, 2], core.Entries.Select(x => x.Index));
        Assert.Equal([1, 3], main.Entries.Select(x => x.Index));
    }

    [Fact]
    public void Build_ShouldPutUnlabelledItemsInDefaultSection()
    {
        var items = new List<SidebarMenuItemModel>
        {
            new() { Id = 1, Name = "沒標 section 的項目" },
            new() { Id = 2, Name = "空白 section", Section = "   " },
        };

        var sections = SidebarMenuSectionBuilder.Build(items);

        var only = Assert.Single(sections);
        Assert.Equal(SidebarMenuSectionBuilder.DefaultSectionName, only.Name);
        Assert.Equal(2, only.Entries.Count);
    }

    [Fact]
    public void Build_ShouldReturnEmpty_WhenNoItems()
    {
        Assert.Empty(SidebarMenuSectionBuilder.Build([]));
    }

    #endregion

    #region CloneWith 必須保留 Section

    [Fact]
    public void CloneWith_ShouldPreserveSection()
    {
        // ApplyPermissionStructure 與 FilterAuthorizedMenuItems 各 Clone 一次；
        // 漏掉 Section 會讓分區靜默失效（所有項目掉進預設區塊），且沒有任何錯誤訊息。
        var item = new SidebarMenuItemModel
        {
            Id = 61,
            Name = "會議紀錄",
            Section = "核心功能",
            Url = "/meetings",
            Icon = "mic",
        };

        var twiceCloned = item.CloneWith().CloneWith();

        Assert.Equal("核心功能", twiceCloned.Section);
        Assert.Equal("會議紀錄", twiceCloned.Name);
        Assert.Equal("/meetings", twiceCloned.Url);
    }

    [Fact]
    public void CloneWith_ShouldPreserveSectionOnChildrenToo()
    {
        var item = new SidebarMenuItemModel
        {
            Id = 3,
            Name = "系統管理",
            Section = "功能選單",
            SubMenu = [new SidebarMenuItemModel { Id = 31, Name = "使用者管理", Url = "/myusers" }],
        };

        var cloned = item.CloneWith();

        Assert.Equal("功能選單", cloned.Section);
        Assert.Equal("使用者管理", Assert.Single(cloned.SubMenu).Name);
    }

    #endregion
}

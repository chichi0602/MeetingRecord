using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Admins;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Tests;

/// <summary>
/// 表單的 dirty 判斷（0.4.84）。
///
/// <para>
/// 這支守的是一個**靜默**的失敗模式：判斷失準時不會有例外、不會有紅字，
/// 只會表現成「按取消時該問的沒問」（使用者打好的字就沒了）或「不該問的一直問」
/// （每次關視窗都要多按一下）。前者尤其嚴重，所以下面的設計刻意讓所有不確定的情況
/// 都往「多問一次」倒。
/// </para>
///
/// <para>
/// ⚠️ 最重要的是第一組「快照穩定性」——整套設計的地基是
/// 「同一份資料拍兩次快照要一模一樣」。這也是回歸保險：日後誰在 AdapterModel 上加了
/// 非決定性的計算屬性（<c>public Guid Key =&gt; Guid.NewGuid()</c> 之類），
/// 這裡會立刻變紅；沒有它的話，症狀是使用者開始抱怨「每次取消都跳確認」。
/// </para>
/// </summary>
public sealed class FormDirtyHelperTests
{
    #region 快照穩定性（整套設計的地基）

    [Fact]
    public void Capture_SameInstanceTwice_ShouldBeIdentical()
    {
        var model = BuildMeeting();

        Assert.Equal(FormDirtyHelper.Capture(model), FormDirtyHelper.Capture(model));
    }

    /// <summary>
    /// 每個實際用到的型別都要通過「Clone 之後快照相同」。
    ///
    /// <para>
    /// 專案的編輯隔離慣例是開視窗時 <c>Clone()</c>（速查表 §3），所以拍快照的對象與
    /// 後來比對的對象往往不是同一個實例。這一條不成立的話，整個功能會變成
    /// 「一開啟就是 dirty」。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllClonableModels))]
    public void Capture_ClonedInstance_ShouldMatchOriginal(string label, object model, object clone)
    {
        Assert.Equal(FormDirtyHelper.Capture(model), FormDirtyHelper.Capture(clone));
        Assert.False(FormDirtyHelper.IsDirty(FormDirtyHelper.Capture(model), clone), label);
    }

    public static TheoryData<string, object, object> AllClonableModels()
    {
        var meeting = BuildMeeting();
        var project = BuildProject();
        var todo = BuildTodo();
        var prompt = new PromptTemplateAdapterModel { Id = 4, Name = "標準會議紀錄", Content = "請整理成條列" };
        var user = BuildUser();
        var role = BuildRole();
        var category = new CategoryAdapterModel { Id = 7, Name = "專案會議" };
        var team = new TeamAdapterModel { Id = 8, Name = "研發部" };

        return new TheoryData<string, object, object>
        {
            { nameof(MeetingAdapterModel), meeting, meeting.Clone() },
            { nameof(ProjectAdapterModel), project, project.Clone() },
            { nameof(TodoAdapterModel), todo, todo.Clone() },
            { nameof(PromptTemplateAdapterModel), prompt, prompt.Clone() },
            { nameof(MyUserAdapterModel), user, user.Clone() },
            { nameof(RoleViewAdapterModel), role, role.Clone() },
            { nameof(CategoryAdapterModel), category, category.Clone() },
            { nameof(TeamAdapterModel), team, team.Clone() },
        };
    }

    #endregion

    #region 差異偵測

    [Fact]
    public void IsDirty_ChangedField_ShouldBeTrue()
    {
        var model = BuildMeeting();
        var snapshot = FormDirtyHelper.Capture(model);

        model.Title = "改過的標題";

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_ListItemAdded_ShouldBeTrue()
    {
        // 清單型欄位（常用名詞、與會人員、分類、團隊）是靠旁邊的「新增」鈕改的，
        // 不經過任何 InputBase，所以正是 EditContext.IsModified() 一定漏掉的那一類。
        var model = BuildProject();
        var snapshot = FormDirtyHelper.Capture(model);

        model.GlossaryTerms.Add("新名詞");

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_ListItemsReordered_ShouldBeTrue()
    {
        // 順序有意義：名冊的排列順序會進提示詞，換順序就是改過。
        var model = BuildProject();
        model.Participants = ["甲", "乙"];
        var snapshot = FormDirtyHelper.Capture(model);

        model.Participants = ["乙", "甲"];

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_NullVersusEmptyString_ShouldBeTrue()
    {
        // 刻意不做 null/"" 正規化：使用者在描述欄打了字又全部刪掉，欄位會從 null 變成 ""。
        // 那確實是「動過」，多問一次比自作聰明安全。
        var model = BuildMeeting();
        model.Description = null;
        var snapshot = FormDirtyHelper.Capture(model);

        model.Description = string.Empty;

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_NullableDateAssigned_ShouldBeTrue()
    {
        var model = BuildTodo();
        model.DueDate = null;
        var snapshot = FormDirtyHelper.Capture(model);

        model.DueDate = new DateTime(2026, 12, 31);

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_PermissionDictionaryKeyAdded_ShouldBeTrue()
    {
        // ⚠️ 這條同時釘住一個**已知且接受**的誤判：RoleViewView 的
        // OnPermissionActionChanged 是 node.Actions[action] = value，所以使用者把一個
        // 原本不存在的 key 勾起來、再取消勾，字典會多出 "edit": false ——
        // 語意沒變但快照變了，會多問一次。
        //
        // 接受它，不要為此去改 OnPermissionActionChanged（那是不相關的既有程式碼）。
        // 失敗方向是安全的：多問一次，不是漏問。
        var model = BuildRole();
        var snapshot = FormDirtyHelper.Capture(model);

        model.RolePermission.Groups[0].Permissions[0].Actions["edit"] = false;

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model));
    }

    [Fact]
    public void IsDirty_Unchanged_ShouldBeFalse()
    {
        // 使用者唯一明確加的條件：沒改過就不要問。
        var model = BuildMeeting();

        Assert.False(FormDirtyHelper.IsDirty(FormDirtyHelper.Capture(model), model));
    }

    #endregion

    #region extras（不在 model 上的暫存狀態）

    [Fact]
    public void IsDirty_ExtraChanged_ShouldBeTrue()
    {
        // 「只拖了一個待上傳的檔案、一個欄位都沒改」就是這個情境。
        var model = BuildMeeting();
        var snapshot = FormDirtyHelper.Capture(model, (string?)null);

        Assert.True(FormDirtyHelper.IsDirty(snapshot, model, "會議錄音.mp3:1024"));
    }

    [Fact]
    public void IsDirty_SameExtra_ShouldBeFalse()
    {
        var model = BuildMeeting();
        var snapshot = FormDirtyHelper.Capture(model, "會議錄音.mp3:1024");

        Assert.False(FormDirtyHelper.IsDirty(snapshot, model, "會議錄音.mp3:1024"));
    }

    [Fact]
    public void Capture_ExtraArityMustStayFixed()
    {
        // 釘住一個容易寫錯的地方：同一個呼叫點拍快照時傳了 extras、比對時忘了傳，
        // 會因為分隔符數量不同而**永遠**判定成 dirty。
        var model = BuildMeeting();

        Assert.NotEqual(FormDirtyHelper.Capture(model), FormDirtyHelper.Capture(model, (string?)null));
    }

    #endregion

    #region null 語意（fail-safe 的方向）

    [Fact]
    public void IsDirty_NullSnapshot_ShouldBeTrue()
    {
        // 忘了在開啟表單時拍快照 → 一律當成改過。
        // 症狀是「每次取消都問」，一試就發現；反過來的失敗模式是靜默吃掉使用者的輸入。
        Assert.True(FormDirtyHelper.IsDirty(null, BuildMeeting()));
    }

    [Fact]
    public void Capture_NullModel_ShouldRoundTrip()
    {
        var snapshot = FormDirtyHelper.Capture(null);

        Assert.Equal("null", snapshot);
        Assert.False(FormDirtyHelper.IsDirty(snapshot, null));
    }

    #endregion

    #region 確認框文案

    [Fact]
    public void BuildDiscardOptions_ShouldFollowTheShippedShape()
    {
        var options = FormDirtyHelper.BuildDiscardOptions("這筆分類");

        Assert.Equal("放棄編修", options.Title);
        Assert.Equal("放棄", options.OkText);
        Assert.Equal("繼續編修", options.CancelText);
        Assert.False(options.MaskClosable);
        Assert.True(options.OkButtonProps.Danger);

        var content = options.Content.AsT0;
        Assert.StartsWith("這筆分類", content);
        Assert.EndsWith("確定要放棄嗎？", content);
    }

    [Fact]
    public void BuildDiscardOptions_WithExtraWarning_ShouldKeepTheClosingQuestion()
    {
        var options = FormDirtyHelper.BuildDiscardOptions(
            "抽出的待辦清單", "關閉之後再開啟會重新抽取一次，並且再計費一次。");

        var content = options.Content.AsT0;
        Assert.Contains("再計費一次", content);
        Assert.EndsWith("確定要放棄嗎？", content);
    }

    [Fact]
    public void BuildDiscardOptions_ShouldMatchTheWordingAlreadyShippedInMarkdownEditorModal()
    {
        // ⚠️ 這一句在 0.4.82 就已經出貨給使用者看過了。helper 的文案必須與它一字不差，
        // 否則同一件事會有兩種說法（會議紀錄編修視窗一種、其他表單另一種）。
        const string shipped = "這份會議紀錄已經改過但還沒儲存，關閉之後改動會消失。確定要放棄嗎？";

        Assert.Equal(shipped, FormDirtyHelper.BuildDiscardOptions("這份會議紀錄").Content.AsT0);
    }

    #endregion

    #region 測試資料

    private static MeetingAdapterModel BuildMeeting() => new()
    {
        Id = 1,
        Title = "週會",
        MeetingDate = new DateTime(2026, 9, 18),
        Description = "描述",
        Categories = ["專案會議"],
        Teams = ["研發部"],
        DraftContent = "## 會議紀錄",
    };

    private static ProjectAdapterModel BuildProject() => new()
    {
        Id = 2,
        Title = "病患篩選",
        Owner = "王小明",
        Status = "進行中",
        CompletionPercentage = 40,
        GlossaryTerms = ["影像報告"],
        Participants = ["王小明", "李小華"],
        Files = [new ProjectFileAdapterModel { Id = 10, OriginalFileName = "規格.pdf", FileSize = 2048 }],
    };

    private static TodoAdapterModel BuildTodo() => new()
    {
        Id = 3,
        Title = "完成篩選功能",
        Description = "描述",
        ProjectId = 2,
        Owner = "王小明",
        Priority = "高",
        Status = "進行中",
    };

    private static MyUserAdapterModel BuildUser() => new()
    {
        Id = 5,
        Account = "support",
        Name = "支援人員",
        RoleViewId = 6,
        RoleView = BuildRole(),
        AdditionalRoleIds = [7],
        TeamNames = ["研發部"],
    };

    private static RoleViewAdapterModel BuildRole() => new()
    {
        Id = 6,
        Name = "管理員",
        DefaultTeams = ["研發部"],
        RolePermission = new RolePermission
        {
            Groups =
            [
                new RolePermissionGroup
                {
                    Name = "會議管理",
                    Enable = true,
                    Permissions =
                    [
                        new RolePermissionNode
                        {
                            Name = "會議紀錄",
                            Enable = false,
                            Actions = new Dictionary<string, bool> { ["view"] = true },
                        },
                    ],
                },
            ],
        },
    };

    #endregion
}

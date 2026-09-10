using System.ComponentModel.DataAnnotations;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Tests;

/// <summary>
/// 內建提示詞範本的單元測試。這些範本是靜態內容，不會有執行期錯誤，
/// 所以測試守的全是「錯了不會壞、只會默默失效」的地方——尤其是漏掉
/// {{transcript}} 這種模型完全拿不到逐字稿、卻不會報錯的情況。
/// </summary>
public sealed class PromptTemplatePresetsTests
{
    [Fact]
    public void All_ShouldContainFivePresets()
    {
        // 數量本身不重要，但改動時應該是有意識的決定，不是不小心漏了一筆。
        Assert.Equal(5, PromptTemplatePresets.All.Count);
    }

    [Fact]
    public void All_ShouldHaveUniqueNamesIgnoringCase()
    {
        // 提示詞名稱在資料庫是全域唯一（不分大小寫），目錄裡自己撞名的話
        // 一鍵建立會有一筆永遠建不進去。
        var names = PromptTemplatePresets.All.Select(x => x.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_ShouldContainTranscriptVariable()
    {
        // 這是最重要的一支：MeetingDraftJobRunner 只做 PromptVariableHelper.Render，
        // 逐字稿只有在範本含 {{transcript}} 時才會被代入。漏了它模型會拿到一份
        // 沒有逐字稿的指令，然後憑空生出一份會議紀錄——不會報錯，也很難被發現。
        Assert.All(PromptTemplatePresets.All, preset =>
            Assert.Contains("{{transcript}}", preset.Content));
    }

    [Fact]
    public void All_ShouldUseOnlyKnownVariables()
    {
        // 用到不支援的變數時，使用者每次儲存這筆範本都會被彈未知變數警告。
        Assert.All(PromptTemplatePresets.All, preset =>
            Assert.Empty(PromptVariableHelper.FindUnknownVariables(preset.Content)));
    }

    [Fact]
    public void All_ShouldSatisfyAdapterModelValidation()
    {
        // 刻意跑 Validator 而不是硬寫 100／20000／2000：長度上限改動時
        // 只有 PromptTemplateAdapterModel 一個地方要動。
        Assert.All(PromptTemplatePresets.All, preset =>
        {
            var model = new PromptTemplateAdapterModel
            {
                Name = preset.Name,
                Content = preset.Content,
                Description = preset.Description,
            };

            var results = new List<ValidationResult>();
            var isValid = Validator.TryValidateObject(model, new ValidationContext(model), results, true);

            Assert.True(isValid, $"「{preset.Name}」不符合驗證：{string.Join("；", results.Select(x => x.ErrorMessage))}");
        });
    }
}

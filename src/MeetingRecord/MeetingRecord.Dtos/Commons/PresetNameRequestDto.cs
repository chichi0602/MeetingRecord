namespace MeetingRecord.Dtos.Commons;

/// <summary>只新增一個內建提示詞範本（1.1.9，<c>POST api/v1/PromptTemplate/presets/single</c>）。</summary>
public sealed class PresetNameRequestDto
{
    public string Name { get; set; } = string.Empty;
}

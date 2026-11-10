namespace MeetingRecord.Dtos.Commons;

/// <summary>下拉選單用的團隊選項（1.1.7）。</summary>
public sealed class TeamOptionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>清單上直接切換啟用狀態（1.1.7，<c>PUT …/{id}/enabled</c>）。</summary>
public sealed class SetEnabledRequestDto
{
    public bool IsEnabled { get; set; }
}

namespace MeetingRecord.Dtos.Commons;

/// <summary>團隊成員多選的選項（1.1.8）：啟用中的帳號，<see cref="Label"/> 是「姓名（帳號）」。</summary>
public sealed class MemberOptionDto
{
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;
}

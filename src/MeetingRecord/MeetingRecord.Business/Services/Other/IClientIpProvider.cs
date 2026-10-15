namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 取得目前請求（或 Blazor 連線）的來源 IP（1.0.32，稽核紀錄用）。實作在 Web 層。
/// 取不到（背景排程、沒有連線資訊）時回傳 null。
/// </summary>
public interface IClientIpProvider
{
    string? GetClientIp();
}

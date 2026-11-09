namespace MeetingRecord.Dtos.Sessions;

/// <summary>
/// 側邊選單的一個項目（<c>GET /api/v1/session/menu</c>，1.1.0）。伺服器已依目前登入者的權限過濾，前端照著畫即可。
/// </summary>
public class MenuItemDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Icon { get; set; }

    public string? Url { get; set; }

    /// <summary>側邊欄的區塊標題，只有頂層節點有。</summary>
    public string? Section { get; set; }

    public List<MenuItemDto> SubMenu { get; set; } = [];
}

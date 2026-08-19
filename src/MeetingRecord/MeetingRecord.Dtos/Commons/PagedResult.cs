using System;
using System.Collections.Generic;
using System.Text;

namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 分頁結果
/// </summary>
public class PagedResult<T>
{
    /// <summary>
    /// 資料清單
    /// </summary>
    public List<T> Items { get; set; } = new();

    /// <summary>
    /// 總筆數
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 當前頁碼
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// 每頁筆數
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// 總頁數
    /// </summary>
    public int TotalPages { get; set; }

    /// <summary>
    /// 是否有上一頁
    /// </summary>
    public bool HasPreviousPage => PageIndex > 1;

    /// <summary>
    /// 是否有下一頁
    /// </summary>
    public bool HasNextPage => PageIndex < TotalPages;
}

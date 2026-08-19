using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Models.Systems;

//
// 摘要:
//     Defines the members of the query.
//
// 備註:
//     DataManagerRequest is used to model bind posted data at server side.
public class DataRequest
{
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = MagicObjectHelper.PageSize;
    //     Specifies the records to skip.
    int Skip { get; set; }

    //     Specifies the records to take.
    public int Take { get; set; }

    //     Sepcifies that the count is required in response.
    public bool RequiresCounts { get; set; }

    //     Specifies the search criteria.
    public string Search { get; set; } = string.Empty;

    //     指定排序欄位名稱
    public string SortField { get; set; } = string.Empty;

    //     指定是否為遞減排序
    public bool? SortDescending { get; set; }

    //     依「分類」過濾（選取多個時以 OR 比對，含任一即符合）
    public List<string> CategoryFilters { get; set; } = [];

    //     依「團隊」過濾（選取多個時以 OR 比對，含任一即符合）
    public List<string> TeamFilters { get; set; } = [];
}

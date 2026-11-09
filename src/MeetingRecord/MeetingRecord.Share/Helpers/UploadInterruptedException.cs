namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 瀏覽器分塊上傳被中斷（1.0.67）：瀏覽器放棄（登入失效、重試用完、伺服器拒收）或太久沒收到資料。
/// <see cref="Exception.Message"/> 就是要給使用者看的原因（例如「登入已失效，請重新登入後再上傳。」）。
///
/// <para>
/// Web 層的上傳工作階段丟出、服務層認得它就把原因帶到畫面與右下角面板；以前一律換成「影音檔上傳失敗」，
/// 登入過期的使用者不知道要重新登入。放在 Share 是因為 Business 不能參考 Web。
/// 繼承 <see cref="IOException"/>：沒認它的地方仍當一般讀取失敗處理。
/// </para>
/// </summary>
public sealed class UploadInterruptedException(string message) : IOException(message);

using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 例外發生當下的情境（誰、哪一頁、哪種來源）。
///
/// 為什麼需要它：各處的 <c>catch (Exception ex) { logger.LogError(ex, "Failed to …"); }</c>
/// 只帶得出「哪個模組」，帶不出「哪個使用者在哪一頁」。情境必須由外層先放好，
/// 記錄發生時再讀回來。
/// </summary>
public sealed record ExceptionContext(string Source, string? Page, string? Account, int? UserId);

/// <summary>
/// 以 <see cref="AsyncLocal{T}"/> 承載 <see cref="ExceptionContext"/>。
///
/// 設定點有三處：<c>ApplicationCircuitHandler.CreateInboundActivityHandler</c>（Blazor 互動）、
/// <c>UseHttpRequestLogging</c>（HTTP／API）、<c>Program.cs</c> 的啟動區段。
/// 三者都在「工作開始前」設定，所以該次工作內部的所有 <c>LogError</c> 都讀得到。
///
/// ⚠️ <b>Suppressed 是防遞迴的第二道保險</b>：背景寫入器執行期間全程開啟，
/// 讓寫入過程中自己拋出的例外不會再被收進管線。
/// <para>⚠️ 狀態放在 static AsyncLocal：所有實例讀寫同一份。DI 註冊成 Singleton；
/// 背景服務（0.4.122 的三個 worker）直接 <c>new</c> 一個來 <c>Set</c> 也是同一份，不必為此改建構子。</para>
/// </summary>
public sealed class ExceptionContextAccessor
{
    private static readonly AsyncLocal<ExceptionContext?> current = new();
    private static readonly AsyncLocal<bool> suppressed = new();

    /// <summary>目前的情境；未設定時為 null，記錄端應退回「未知」。</summary>
    public ExceptionContext? Current => current.Value;

    /// <summary>是否正處於「不得收錄」的區段。</summary>
    public bool IsSuppressed => suppressed.Value;

    public void Set(ExceptionContext context) => current.Value = context;

    public void Clear() => current.Value = null;

    /// <summary>
    /// 進入抑制區段。回傳的 <see cref="IDisposable"/> 釋放時自動還原。
    /// 用法：<c>using var _ = accessor.Suppress();</c>
    /// </summary>
    public IDisposable Suppress()
    {
        var previous = suppressed.Value;
        suppressed.Value = true;
        return new SuppressionScope(previous);
    }

    private sealed class SuppressionScope : IDisposable
    {
        private readonly bool previous;
        private bool disposed;

        public SuppressionScope(bool previous) => this.previous = previous;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            suppressed.Value = previous;
        }
    }
}

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecord.Web.Streaming;

/// <summary>串流跑完的結果，給呼叫端事後寫稽核：<see cref="Error"/> 是 null 表示成功。</summary>
public sealed record NdjsonStreamOutcome<T>(T? Result, Exception? Error);

/// <summary>
/// 邊產生邊回傳的 AI 回答（1.1.29 從 <c>AiChatController</c> 抽出，AI 問答與使用說明 AI 助理共用）。
///
/// <para>
/// 服務層邊產生邊回報的文字經 <see cref="Channel"/> 轉成一行一筆 JSON（<c>application/x-ndjson</c>）。
/// 等到第一段文字或工作結束才決定：第一段文字出來之前就失敗，照一般 API 回錯誤（由呼叫端決定回哪個 <see cref="IActionResult"/>）；
/// 開始寫回應之後失敗，送一筆錯誤事件。
/// </para>
///
/// <para>
/// 使用者離開頁面、按「停止」時連線會斷：寫回應的例外吞掉；請求的取消權杖要由呼叫端傳給服務層，產生才會跟著停（不再繼續花費）。
/// </para>
/// </summary>
public static class NdjsonStreamer
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    /// <param name="run">實際產生回答；參數是逐段回報新文字的委派。</param>
    /// <param name="deltaEvent">一段文字 → 事件。</param>
    /// <param name="doneEvent">完成 → 事件。</param>
    /// <param name="failureBeforeStart">第一段文字前就失敗 → 一般的錯誤回應。</param>
    /// <param name="errorEvent">開始之後失敗 → 錯誤事件（需要記 log 的在這裡記）。</param>
    public static async Task<NdjsonStreamOutcome<T>> RunAsync<T>(
        ControllerBase controller,
        Func<Action<string>, Task<T>> run,
        Func<string, object> deltaEvent,
        Func<T, object> doneEvent,
        Func<Exception, IActionResult> failureBeforeStart,
        Func<Exception, object> errorEvent,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var work = Task.Run(async () =>
        {
            try
            {
                return await run(delta => channel.Writer.TryWrite(delta));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // 等到第一段文字或工作結束，才決定要回一般錯誤還是開始串流。
        var hasDelta = await channel.Reader.WaitToReadAsync(CancellationToken.None);
        if (!hasDelta)
        {
            // 通道關了代表工作已經結束，但 Task 的狀態要等 finally 跑完才落定：先等它，再看成敗。
            try
            {
                await work;
            }
            catch
            {
                // 下面看 IsFaulted。
            }
        }

        if (!hasDelta && work.IsFaulted)
        {
            var error = work.Exception!.GetBaseException();
            await failureBeforeStart(error).ExecuteResultAsync(controller.ControllerContext);
            return new NdjsonStreamOutcome<T>(default, error);
        }

        var response = controller.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/x-ndjson; charset=utf-8";
        await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            await WriteEventAsync(response, deltaEvent(delta), cancellationToken);
        }

        try
        {
            var result = await work;
            await WriteEventAsync(response, doneEvent(result), cancellationToken);
            return new NdjsonStreamOutcome<T>(result, null);
        }
        catch (Exception ex)
        {
            await WriteEventAsync(response, errorEvent(ex), cancellationToken);
            return new NdjsonStreamOutcome<T>(default, ex);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, object item, CancellationToken cancellationToken)
    {
        try
        {
            await response.WriteAsync(JsonSerializer.Serialize(item, item.GetType(), StreamJson) + "\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 使用者離開頁面、按停止、連線斷了：請求的取消權杖也傳給了服務層，產生會跟著停。
        }
    }
}

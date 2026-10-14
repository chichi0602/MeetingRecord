// 瀏覽器端錯誤回報（0.4.122，移植自 NET10-Blazor-Starter）。
// 掛上 window 'error' 與 'unhandledrejection'，經 Blazor circuit 回報給伺服器的 BrowserErrorReporter，
// 以 Error 記進日誌檔與系統例外紀錄（來源「瀏覽器」）。
//
// 只有登入後的頁面（MainLayout）會呼叫 register；其他頁面的錯誤只會暫存在記憶體（上限 20 筆），不會送出。
// 長度上限與過濾在這裡先做一次，伺服器端還會再做一次（不信任前端）。
(function () {
    const maxBuffered = 20;
    const maxMessageLength = 1000;
    const maxStackLength = 4000;
    const buffer = [];
    let reporter = null;

    // 瀏覽器外掛注入的腳本、已知無害的瀏覽器訊息，不是本系統的錯誤。
    const extensionPattern = /(chrome|moz|safari|ms-browser|edge)-extension:\/\//i;
    const ignoredMessages = [/ResizeObserver loop/i, /^Script error\.?$/i];

    function shouldIgnore(message, stack, source) {
        if (extensionPattern.test(source || '') || extensionPattern.test(stack || '')) {
            return true;
        }

        return ignoredMessages.some(pattern => pattern.test(message || ''));
    }

    function send(item) {
        // 回報本身失敗（circuit 已斷線、元件已釋放）不可再丟出錯誤，否則會自己觸發自己。
        reporter.invokeMethodAsync('Report', item.kind, item.message, item.stack, item.source, item.path)
            .catch(() => { });
    }

    function capture(kind, message, stack, source) {
        if (shouldIgnore(message, stack, source)) {
            return;
        }

        const item = {
            kind: kind,
            message: String(message || '(no message)').slice(0, maxMessageLength),
            stack: String(stack || '').slice(0, maxStackLength),
            source: String(source || '').slice(0, 500),
            path: window.location.pathname
        };

        if (reporter) {
            send(item);
        } else if (buffer.length < maxBuffered) {
            buffer.push(item);
        }
    }

    window.addEventListener('error', event => {
        const location = event.filename ? `${event.filename}:${event.lineno}:${event.colno}` : '';
        capture('error', event.message, event.error && event.error.stack, location);
    });

    window.addEventListener('unhandledrejection', event => {
        const reason = event.reason;
        const message = reason && reason.message ? reason.message : String(reason);
        capture('unhandledrejection', message, reason && reason.stack, '');
    });

    // 刻意不提供 unregister：版面切換時舊參考的釋放與新參考的註冊順序不固定，
    // 由「後註冊者取代前者」即可；舊參考已釋放時 send 的失敗會被吞掉。
    window.meetingRecordClientErrors = {
        register: (dotNetReference) => {
            reporter = dotNetReference;
            while (buffer.length > 0) {
                send(buffer.shift());
            }
        }
    };
})();

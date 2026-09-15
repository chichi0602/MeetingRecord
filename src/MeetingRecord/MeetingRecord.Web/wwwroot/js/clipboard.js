// 複製到剪貼簿。
//
// ⚠️ 這件事在 Blazor Server 上比想像中容易失敗：點擊事件先送到伺服器、處理完再回來呼叫
// 這個函式，此時瀏覽器認定的「使用者手勢」可能已經過期。Chromium 對作用中的分頁通常
// 還是放行，Firefox 則多半會擋。非安全來源（區網 HTTP）連 navigator.clipboard 都沒有。
//
// 所以這裡回傳布林而不是靜靜吞掉例外——呼叫端要依結果決定顯示成功還是「請手動選取」。
window.meetingRecordClipboard = {
    copyText: async function (text) {
        const value = text ?? '';

        // 首選：非同步的 Clipboard API。需要安全來源（https 或 localhost）。
        if (navigator.clipboard && window.isSecureContext) {
            try {
                await navigator.clipboard.writeText(value);
                return true;
            } catch {
                // 手勢過期或使用者拒絕授權，往下走退路。
            }
        }

        // 退路：已棄用但相容性最好的 execCommand。同樣需要手勢，不保證成功。
        try {
            const textarea = document.createElement('textarea');
            textarea.value = value;
            // 不能用 display:none，那樣選取不到；移到畫面外並避免捲動跳動。
            textarea.setAttribute('readonly', '');
            textarea.style.position = 'fixed';
            textarea.style.top = '-1000px';
            textarea.style.opacity = '0';
            document.body.appendChild(textarea);

            textarea.select();
            textarea.setSelectionRange(0, value.length);

            const copied = document.execCommand('copy');
            textarea.remove();

            return copied;
        } catch {
            return false;
        }
    }
};

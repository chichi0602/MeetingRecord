// WASM 頁的載入畫面（1.1.58）：#app-loading 由 App.razor 靜態送出，WASM 跑起來之前就看得到。
// - 超過 20 秒還在，顯示提示與「重新整理」（網路很慢或下載失敗時，使用者才知道不是自己操作錯）。
// - WasmLayout 拿到登入狀態後呼叫 meetingRecordAppLoading.hide() 移除。
(function () {
    'use strict';

    var hintDelayMs = 20000;
    var timer = null;

    if (document.getElementById('app-loading')) {
        timer = setTimeout(function () {
            var hint = document.querySelector('#app-loading .app-loading-hint');
            if (hint) {
                hint.hidden = false;
            }
        }, hintDelayMs);
    }

    window.meetingRecordAppLoading = {
        hide: function () {
            if (timer !== null) {
                clearTimeout(timer);
                timer = null;
            }

            var element = document.getElementById('app-loading');
            if (element) {
                element.remove();
            }
        }
    };
})();

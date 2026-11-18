// 1.1.69 完成通知：使用者切到別的分頁時，自己的轉錄／會議紀錄完成了，就在分頁標題前加「(✓) 」，
// 回到這個分頁就拿掉。頁面在前景時不動標題（右下角已經跳提示了）。
window.meetingRecordTabAlert = (function () {
    const prefix = '(✓) ';
    let listening = false;

    function clear() {
        if (document.title.startsWith(prefix)) {
            document.title = document.title.substring(prefix.length);
        }
    }

    return {
        flag: function () {
            if (!document.hidden) {
                return;
            }

            if (!document.title.startsWith(prefix)) {
                document.title = prefix + document.title;
            }

            if (!listening) {
                listening = true;
                document.addEventListener('visibilitychange', function () {
                    if (!document.hidden) {
                        clear();
                    }
                });
            }
        }
    };
})();

// 1.1.68 成本閘門：上傳前在瀏覽器讀出影音檔長度（秒），讓確認視窗能先顯示預估轉錄費用。
//
// 用 <audio>／<video> 只讀中繼資料（preload=metadata），不會把整個檔案讀進記憶體。
// 讀不到（瀏覽器不支援的格式、長度是 Infinity 的串流格式、逾時）一律回 null——
// 呼叫端照舊確認、只是不顯示金額。真正的把關在伺服器轉檔量到長度之後。
window.meetingRecordMedia = {
    getDuration: function (input, timeoutMs) {
        return new Promise(function (resolve) {
            const file = input && input.files && input.files[0];
            if (!file) {
                resolve(null);
                return;
            }

            const url = URL.createObjectURL(file);
            const element = document.createElement((file.type || '').startsWith('video') ? 'video' : 'audio');
            let done = false;
            let timer = 0;

            const finish = function (value) {
                if (done) {
                    return;
                }

                done = true;
                clearTimeout(timer);
                element.removeAttribute('src');
                URL.revokeObjectURL(url);
                resolve(value);
            };

            timer = setTimeout(function () { finish(null); }, timeoutMs || 5000);
            element.preload = 'metadata';
            element.onloadedmetadata = function () {
                const seconds = element.duration;
                finish(Number.isFinite(seconds) && seconds > 0 ? seconds : null);
            };
            element.onerror = function () { finish(null); };
            element.src = url;
        });
    }
};

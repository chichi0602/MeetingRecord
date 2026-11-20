// 1.1.71：在瀏覽器直接錄音（上傳視窗的「錄音」）。
//
// 錄好的聲音留在 JS 這一端（Blob），不經過 .NET：上傳時 chunk-upload.js 用 take(key) 取走、照原本的分塊上傳送出。
// ⚠️ start／stop 都立即返回：JS 互操作預設 1 分鐘逾時，錄音一錄就是一小時，不能讓 .NET 那邊等著。
// ⚠️ getUserMedia 只能在安全來源（HTTPS 或 localhost）用。
window.meetingRecordRecorder = (function () {
    // 優先 webm/opus（Chrome、Edge、Firefox），Safari 只有 mp4。副檔名要在 MeetingMediaPolicy 允許的清單裡。
    const candidates = [
        { mimeType: "audio/webm;codecs=opus", extension: ".webm" },
        { mimeType: "audio/webm", extension: ".webm" },
        { mimeType: "audio/ogg;codecs=opus", extension: ".ogg" },
        { mimeType: "audio/mp4", extension: ".m4a" }
    ];

    const recordings = new Map();
    let current = null;
    let nextKey = 1;

    function warnBeforeUnload(event) {
        event.preventDefault();
        event.returnValue = "";
    }

    function pickFormat() {
        if (typeof MediaRecorder === "undefined") {
            return null;
        }

        return candidates.find(x => MediaRecorder.isTypeSupported(x.mimeType)) || null;
    }

    function releaseStream(stream) {
        stream.getTracks().forEach(track => track.stop());
    }

    return {
        // 回傳 null 代表成功開始；否則回傳給使用者看的原因。
        start: async function () {
            if (current) {
                return "已經在錄音了。";
            }

            const format = pickFormat();
            if (!format || !window.isSecureContext || !navigator.mediaDevices) {
                return "這個瀏覽器不支援錄音（需要 HTTPS，建議用 Chrome 或 Edge）。";
            }

            let stream;
            try {
                stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            } catch (error) {
                return error && error.name === "NotAllowedError"
                    ? "沒有取得麥克風權限，請在瀏覽器網址列旁允許使用麥克風後再試一次。"
                    : "找不到可以用的麥克風。";
            }

            const recorder = new MediaRecorder(stream, { mimeType: format.mimeType });
            const chunks = [];
            recorder.ondataavailable = event => {
                if (event.data && event.data.size > 0) {
                    chunks.push(event.data);
                }
            };

            current = { recorder, stream, chunks, format };
            // 每秒收一塊：分頁當掉時頂多少一秒，也避免最後一次把整段塞進記憶體。
            recorder.start(1000);
            window.addEventListener("beforeunload", warnBeforeUnload);
            return null;
        },

        // 停止並回傳 { key, size, mimeType, extension }；沒有在錄音時回 null。
        stop: function () {
            if (!current) {
                return Promise.resolve(null);
            }

            const { recorder, stream, chunks, format } = current;
            current = null;
            window.removeEventListener("beforeunload", warnBeforeUnload);

            return new Promise(resolve => {
                recorder.onstop = () => {
                    releaseStream(stream);
                    const blob = new Blob(chunks, { type: format.mimeType.split(";")[0] });
                    const key = String(nextKey++);
                    recordings.set(key, blob);
                    resolve({ key: key, size: blob.size, mimeType: blob.type, extension: format.extension });
                };
                recorder.stop();
            });
        },

        // 錄音中取消（關視窗、按取消）：什麼都不留。
        cancel: function () {
            if (!current) {
                return;
            }

            const { recorder, stream } = current;
            current = null;
            window.removeEventListener("beforeunload", warnBeforeUnload);
            recorder.onstop = () => releaseStream(stream);
            recorder.stop();
        },

        // 錄好但不要了（重錄、移除、關視窗）。
        discard: function (key) {
            recordings.delete(key);
        },

        // 上傳開始不了時把錄音存到使用者電腦（錄音沒有第二份，丟掉就沒了）。
        saveToDisk: function (key, fileName) {
            const blob = recordings.get(key);
            if (!blob) {
                return;
            }

            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
        },

        // 上傳開始時取走（取走就從這裡移除，上傳那邊持有參照直到送完）。
        take: function (key) {
            const blob = recordings.get(key) || null;
            recordings.delete(key);
            return blob;
        }
    };
})();

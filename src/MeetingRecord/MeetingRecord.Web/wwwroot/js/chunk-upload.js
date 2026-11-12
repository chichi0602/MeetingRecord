// 分塊上傳（1.0.59）：影音檔與專案附件用 HTTP 一塊一塊送，不經長連線（SignalR）串流。
// 經連線串流上傳時，IIS 上 127 MB 的檔案約 11 秒就斷線，還會拖垮整條連線。
//
// 由 C# 呼叫（WASM 端的 MeetingUploadManager／ProjectAttachmentUploader，1.1.23）：start() 只登記並啟動送檔迴圈就回來（不讓 C# 等整個檔案傳完，
// JS interop 預設 1 分鐘逾時）。伺服器端把收到的分塊接成一個 Stream 交給服務層讀，
// 所以「傳完了沒、成功還是失敗」由服務層決定；這裡只在送不下去時回呼 OnFailed。
//
// 伺服器回 404／409 表示那一頭已經不要了（存完、被拒絕、被取消、逾時），安靜收手即可。
window.meetingRecordChunkUpload = (function () {
    function warnBeforeUnload(event) {
        event.preventDefault();
        event.returnValue = '';
    }

    const headerName = "X-MeetingRecord-Upload";
    const retryDelays = [1000, 3000, 9000];
    const active = new Map();

    function fatal(message) {
        const error = new Error(message);
        error.fatal = true;
        return error;
    }

    function sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    async function readMessage(response, fallback) {
        try {
            const body = await response.json();
            return body?.errorMessage || body?.message || fallback;
        } catch {
            return fallback;
        }
    }

    // 回傳 true＝收下了；false＝伺服器那一頭不要了，該收手；送不下去就丟 fatal。
    async function sendChunk(uploadId, index, blob, state) {
        const url = new URL(`api/v1/uploads/${uploadId}/chunks/${index}`, document.baseURI);
        for (let attempt = 0; ; attempt++) {
            if (state.aborted) {
                return false;
            }

            try {
                const response = await fetch(url, {
                    method: "PUT",
                    body: blob,
                    credentials: "same-origin",
                    redirect: "manual",
                    signal: state.controller.signal,
                    headers: { [headerName]: "1", "Content-Type": "application/octet-stream" },
                });

                // 登入 Cookie 失效時驗證中介層會導向登入頁（manual 之下拿到 opaqueredirect）。
                if (response.type === "opaqueredirect" || response.status === 401) {
                    throw fatal("登入已失效，請重新登入後再上傳。");
                }
                if (response.ok) {
                    return true;
                }
                if (response.status === 404 || response.status === 409) {
                    return false;
                }
                if (response.status === 400 || response.status === 413) {
                    throw fatal(await readMessage(response, "上傳的資料不正確，請重新上傳。"));
                }
                // 其他（5xx、閘道逾時）視為暫時性錯誤，重試。
            } catch (error) {
                if (error.fatal) {
                    throw error;
                }
                if (state.aborted) {
                    return false;
                }
                // 網路錯誤：重試。
            }

            if (attempt >= retryDelays.length) {
                throw fatal("網路不穩，重試多次仍無法上傳，請稍後重新上傳。");
            }
            await sleep(retryDelays[attempt]);
        }
    }

    async function run(file, uploadId, chunkSize, dotnetRef, state) {
        try {
            const chunkCount = Math.ceil(file.size / chunkSize);
            for (let index = 0; index < chunkCount; index++) {
                const start = index * chunkSize;
                const blob = file.slice(start, Math.min(start + chunkSize, file.size));
                if (!await sendChunk(uploadId, index, blob, state)) {
                    return;
                }
            }
        } catch (error) {
            if (!state.aborted) {
                try {
                    await dotnetRef.invokeMethodAsync("OnFailed", error.fatal ? error.message : "上傳失敗，請重新上傳。");
                } catch {
                    // 頁面已經離開，伺服器端會因為逾時自己結束這趟上傳。
                }
            }
        } finally {
            active.delete(uploadId);
        }
    }

    return {
        start: function (input, fileIndex, fileName, fileSize, uploadId, chunkSize, dotnetRef) {
            // 在呼叫當下就拿到 File 物件：之後表單被重設、input 被重建也不影響這趟上傳。
            const file = input && input.files ? input.files[fileIndex] : null;
            if (!file || file.name !== fileName || file.size !== fileSize) {
                dotnetRef.invokeMethodAsync("OnFailed", "找不到要上傳的檔案，請重新選擇檔案。").catch(() => { });
                return;
            }

            const state = { aborted: false, controller: new AbortController() };
            active.set(uploadId, state);
            run(file, uploadId, chunkSize, dotnetRef, state);
        },

        // 1.1.6：上傳進行中關分頁或重新整理時，請瀏覽器跳出「確定要離開嗎？」。
        // 由第一個上傳開始時打開、最後一個結束時關掉（WASM 端的 MeetingUploadManager 呼叫）。
        setLeaveGuard: function (active) {
            if (active) {
                window.addEventListener('beforeunload', warnBeforeUnload);
            } else {
                window.removeEventListener('beforeunload', warnBeforeUnload);
            }
        },

        abort: function (uploadId) {
            const state = active.get(uploadId);
            if (state) {
                state.aborted = true;
                state.controller.abort();
                active.delete(uploadId);
            }
        },
    };
})();

// 檔案下載輔助：把伺服器端串流過來的內容變成瀏覽器的下載動作。
//
// 之所以走 JS interop 而不是開 HTTP 端點：本專案所有 API controller 都是
// JWT Bearer 驗證，瀏覽器導航帶的是 Cookie，<a href> 一定 401；而且多開一個
// 對外的檔案輸出面就多一處要顧的授權。這樣做檔案完全不落地。
window.meetingRecordDownload = {
    // contentStreamReference 由 .NET 端以 DotNetStreamReference 傳入
    saveAsFile: async function (fileName, contentStreamReference) {
        const arrayBuffer = await contentStreamReference.arrayBuffer();
        const blob = new Blob([arrayBuffer], { type: 'text/markdown;charset=utf-8' });
        const url = URL.createObjectURL(blob);

        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName ?? 'download';
        document.body.appendChild(anchor);
        anchor.click();

        anchor.remove();
        // 立刻釋放，否則 blob 會留在記憶體直到整頁卸載
        URL.revokeObjectURL(url);
    }
};

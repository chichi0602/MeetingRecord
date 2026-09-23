// AI 問答的附件：貼上（Ctrl+V）與拖放（0.4.95）。
//
// 做法與 FileDropZone 同一個思路：不自己把檔案傳給 .NET，而是把檔案塞進容器裡那個
// Blazor <InputFile> 的 input.files，再觸發 change——後面讀檔、驗證全部走 InputFile 的 OnChange，
// 挑選、貼上、拖放三條路只有一個入口。
//
// ⚠️ input 每次用完會被 Blazor 以 @key 重建，所以不能在 attach 時記住它，
//    一律在事件發生當下用 querySelector 找。
(function () {
    const pad = (n) => String(n).padStart(2, '0');

    // 截圖貼上時瀏覽器給的檔名一律是 image.png：一次貼多張會撞名，事後也分不出是哪一張。
    function nameFor(file, index, total) {
        if (file.name && file.name !== 'image.png') {
            return file.name;
        }

        const now = new Date();
        const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
        const ext = ((file.type || 'image/png').split('/')[1] || 'png').replace('jpeg', 'jpg');
        return `貼上的圖片-${stamp}${total > 1 ? `-${index + 1}` : ''}.${ext}`;
    }

    // 把檔案交給容器內的 <input type="file">。回傳是否真的交出去了。
    function handOver(container, files) {
        const input = container.querySelector('input[type="file"]');
        if (!input || input.disabled || files.length === 0) {
            return false;
        }

        const transfer = new DataTransfer();
        files.forEach((file, index) => {
            transfer.items.add(new File([file], nameFor(file, index, files.length), { type: file.type }));
        });

        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    function onPaste(event) {
        const files = [...(event.clipboardData?.items ?? [])]
            .filter((item) => item.kind === 'file')
            .map((item) => item.getAsFile())
            .filter(Boolean);

        // 純文字貼上照常進輸入框，只有剪貼簿裡有檔案時才攔下來。
        if (files.length > 0 && handOver(event.currentTarget, files)) {
            event.preventDefault();
        }
    }

    function onDragOver(event) {
        if ([...(event.dataTransfer?.types ?? [])].includes('Files')) {
            // 不擋的話瀏覽器會直接在分頁裡打開檔案。
            event.preventDefault();
        }
    }

    function onDrop(event) {
        const files = [...(event.dataTransfer?.files ?? [])];
        if (files.length > 0) {
            event.preventDefault();
            handOver(event.currentTarget, files);
        }
    }

    window.meetingRecordChatAttach = {
        attach: function (container) {
            if (!container || container.__meetingRecordChatAttach) {
                return;
            }

            container.addEventListener('paste', onPaste);
            container.addEventListener('dragover', onDragOver);
            container.addEventListener('drop', onDrop);
            container.__meetingRecordChatAttach = true;
        },
    };
})();

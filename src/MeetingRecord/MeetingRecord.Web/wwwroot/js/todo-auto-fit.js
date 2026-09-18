// 待辦頁：把表格列數調成「剛好吃滿一個畫面」，順便把右邊面板的高度釘在視窗內。
//
// 這裡刻意只做「DOM 才做得到」的事——量高度。要補幾列、要縮幾列的決策留在 C# 的
// TodoAutoFit：本專案沒有 bUnit，放進 JS 的邏輯就永遠測不到（與 text-editor.js 同一條理由）。
window.meetingRecordTodoAutoFit = (function () {
    // 面板高度上限。⚠️ 掛在 <html> 上而不是面板自己的 inline style：
    // 面板是 Blazor 算繪出來的元素，樣式寫在它身上等於跟框架搶同一個位置。
    const PanelVariable = '--todo-view-panel-max';

    // 量測之間的節流。拉視窗大小會連續觸發，每一次都往伺服器跑一趟查詢太貴。
    const DebounceMilliseconds = 150;

    // 底部再多留 2px。scrollHeight 是整數、版面高度是小數，剛剛好算滿的情況下
    // 無條件進位會多出 1px——而 1px 也是捲軸。這 2px 就是為了讓那條捲軸不要出現。
    const SafetyMargin = 2;

    const registry = new WeakMap();

    function measure(root) {
        const wrap = root.querySelector('.todo-view-table-wrap');
        const tbody = root.querySelector('.todo-view-table-wrap .ant-table-tbody');
        if (!wrap || !tbody) {
            return null;
        }

        // 畫面底部一定要留的空白 = 外層 <article> 的 padding-bottom + margin-bottom。
        // 量出來而不是寫死常數，版面改了不用記得同步改這裡。
        let bottomGap = 0;
        const article = root.closest('article');
        if (article) {
            const articleStyle = getComputedStyle(article);
            bottomGap = (parseFloat(articleStyle.paddingBottom) || 0)
                + (parseFloat(articleStyle.marginBottom) || 0);
        }
        const bottomLimit = window.innerHeight - bottomGap - SafetyMargin;

        // ⚠️ rect 是「視窗」座標，會跟著使用者當下捲到哪裡變。加上 scrollY 換成文件座標，
        //    量測結果才不會因為進頁面時剛好捲在半路而算錯。
        //    標題列是 sticky（仍佔著流），所以文件座標就等於「沒捲動時的視窗座標」。
        //
        // 量的是「表格區塊（含分頁器）底部離畫面底還剩多少」，不是「可用高度 ÷ 列高」。
        // 後者要先知道列高，而列高不是固定的（見 TodoAutoFit 的說明），估出來的值
        // 一定偏保守，再被無條件捨去吃掉一列——實跑量到表格底永遠離畫面底 76～177px。
        const leftover = bottomLimit - (wrap.getBoundingClientRect().bottom + window.scrollY);

        // 最高與最矮各回報一個：補列用最高的估（保守，不會補到超出），
        // 縮列用最矮的估（積極，寧可多縮一列也不要留著捲軸）。
        let tallestRow = 0;
        let shortestRow = Number.POSITIVE_INFINITY;
        const rows = root.querySelectorAll('.todo-view-table-wrap .ant-table-tbody tr.ant-table-row');
        for (const row of rows) {
            const height = row.getBoundingClientRect().height;
            tallestRow = Math.max(tallestRow, height);
            shortestRow = Math.min(shortestRow, height);
        }
        if (!rows.length) {
            shortestRow = 0;
        }

        // 右邊面板：量到視窗底為止。內容再多也是面板裡的小清單自己捲，不會把整頁撐長。
        const panel = root.querySelector('.todo-view-owner-panel');
        if (panel) {
            const panelTop = panel.getBoundingClientRect().top + window.scrollY;
            // ⚠️ 無條件捨去，不要四捨五入：進位上去那 1px 也是一條捲軸。
            document.documentElement.style.setProperty(
                PanelVariable,
                Math.floor(Math.max(0, bottomLimit - panelTop)) + 'px');
        }

        return {
            leftover: leftover,
            tallestRow: tallestRow,
            shortestRow: shortestRow,
            // 折不折行取決於欄寬，視窗高度決定放得下幾列——兩個任一變了，C# 端要把上限丟掉重來。
            contentWidth: wrap.getBoundingClientRect().width,
            viewportHeight: window.innerHeight,
        };
    }

    function observe(root, dotNetReference) {
        if (!root || !dotNetReference) {
            return;
        }

        disconnect(root);

        let timer = 0;
        const push = function () {
            const result = measure(root);
            if (result) {
                dotNetReference.invokeMethodAsync(
                    'OnAutoFitMeasuredAsync',
                    result.leftover,
                    result.tallestRow,
                    result.shortestRow,
                    result.contentWidth,
                    result.viewportHeight);
            }
        };
        const schedule = function () {
            window.clearTimeout(timer);
            timer = window.setTimeout(push, DebounceMilliseconds);
        };

        window.addEventListener('resize', schedule);

        // ⚠️ 只在「寬度」變了才重算。高度變化有一大半是我們自己改列數造成的，
        //    不擋掉的話 ResizeObserver 會被自己的結果再叫起來一次。
        //    改完列數要再量一次是靠 C# 明確呼叫 remeasure，不是靠這裡。
        //    先讀一次現值，免得 observe() 當下那一發初始回呼又排一次重複的量測。
        let lastWidth = Math.round(root.getBoundingClientRect().width);
        const resizeObserver = new ResizeObserver(function (entries) {
            const width = Math.round(entries[0].contentRect.width);
            if (width === lastWidth) {
                return;
            }
            lastWidth = width;
            schedule();
        });
        resizeObserver.observe(root);

        registry.set(root, {
            resizeObserver: resizeObserver,
            schedule: schedule,
            cancel: function () { window.clearTimeout(timer); },
        });

        push();
    }

    // C# 改完每頁筆數、畫面重繪之後叫這支，讓控制器看到新的剩餘空間再決定要不要繼續補。
    // ⚠️ 少了它就只會調整一次：ResizeObserver 只看寬度，改列數只會改高度。
    function remeasure(root) {
        const entry = root ? registry.get(root) : null;
        if (entry) {
            entry.schedule();
        }
    }

    function disconnect(root) {
        if (!root) {
            return;
        }

        const entry = registry.get(root);
        if (entry) {
            entry.cancel();
            entry.resizeObserver.disconnect();
            window.removeEventListener('resize', entry.schedule);
            registry.delete(root);
        }

        // 變數是掛在 <html> 上的，離開頁面要自己收掉，不然會留給下一頁一個沒人用的殘值。
        document.documentElement.style.removeProperty(PanelVariable);
    }

    return { observe: observe, remeasure: remeasure, disconnect: disconnect };
})();

// <textarea> 裡的選取與捲動。
//
// 這裡刻意只做「DOM 才做得到」的事：選取、聚焦、捲動，以及左右同步捲動（要量像素）。找字、算索引、取代
// 一律留在 C# 的 TextSearchHelper——本專案沒有 bUnit，放進 JS 的邏輯就永遠測不到。
//
// ⚠️ 索引的單位是 UTF-16 code unit，與 C# 的 string 索引相同，所以兩邊可以直接對接。
// 前提是文字已經正規化成 LF（C# 端的 TextSearchHelper.NormalizeNewLines 負責）：
// <textarea> 的 value 在 DOM 裡一律是 LF，含 \r\n 的字串會從第一個換行起愈偏愈多。
window.meetingRecordTextEditor = {
    // 選取 [start, start + length)，並盡量把它捲進視野。
    selectRange: function (element, start, length) {
        if (!element) {
            return;
        }

        // 先粗估捲動位置。
        // ⚠️ <textarea> 沒有「捲到第 n 個字元」的原生 API，只能用行號 × 行高估算，
        // 而 soft wrap（一個長行佔多個視覺行）會讓這個估算偏低。所以估完之後還要靠
        // setSelectionRange 讓瀏覽器自己把選取處微調進可視範圍——兩段都要，缺一不可。
        const before = element.value.slice(0, start);
        const line = before.split('\n').length - 1;
        const lineHeight = parseFloat(getComputedStyle(element).lineHeight) || 20;

        element.scrollTop = Math.max(0, (line * lineHeight) - (element.clientHeight / 2));

        // preventScroll：focus 本身會把外層容器也捲一次，但我們已經自己算好了，
        // 讓它再捲一次會把整個對話框內容區拉走。
        element.focus({ preventScroll: true });
        element.setSelectionRange(start, start + length);
    },

    // 取代之前記下游標與捲動位置，取代之後還原——「全部取代」才不會讓畫面亂跳。
    getState: function (element) {
        if (!element) {
            return { caret: 0, scrollTop: 0 };
        }

        return { caret: element.selectionStart, scrollTop: element.scrollTop };
    },

    setScrollTop: function (element, value) {
        if (element) {
            element.scrollTop = value;
        }
    },

    // 左邊原文、右邊預覽的同步捲動（0.4.100），對齊到段落而不是按比例。
    //
    // 預覽的每個區塊帶著 data-source-line（原文第幾行，C# 的 MarkdownRenderer 產生）。
    // 兩邊各量出「這一行／這個區塊在第幾個像素」，組成一串對應點；捲動時找出頂端落在
    // 哪兩個對應點之間，照像素比例內插到另一邊。表格、清單在兩邊高度差很多，按比例會漂。
    // 回傳是否真的綁上。對話框剛打開的那一次算繪，內容可能還沒進 DOM，C# 端據此下一輪再試。
    bindScrollSync: function (source, preview) {
        if (!source || !preview || !source.isConnected || !preview.isConnected) {
            return false;
        }

        this.unbindScrollSync(source);

        const state = {
            source: source,
            preview: preview,
            pairs: null,
            // 程式設定捲動位置時也會觸發 scroll 事件，不擋的話兩邊會互相推來推去。
            // 記下「預期的值」而不是一個旗標：值沒變時瀏覽器不會發事件，旗標會卡住吃掉下一次真正的捲動。
            expected: null,
            frame: 0,
            mirror: document.createElement('div')
        };

        state.mirror.setAttribute('aria-hidden', 'true');
        state.mirror.style.cssText = 'position:absolute;top:0;left:-99999px;visibility:hidden;'
            + 'white-space:pre-wrap;overflow-wrap:break-word;word-break:normal;box-sizing:border-box;';
        document.body.appendChild(state.mirror);

        const invalidate = () => { state.pairs = null; };
        const onScroll = (event) => {
            const from = event.target;
            const expected = state.expected;
            if (expected && expected.element === from && Math.abs(from.scrollTop - expected.top) < 2) {
                state.expected = null;
                return;
            }

            cancelAnimationFrame(state.frame);
            state.frame = requestAnimationFrame(() => syncFrom(state, from));
        };

        state.handlers = { invalidate: invalidate, onScroll: onScroll };
        source.addEventListener('scroll', onScroll, { passive: true });
        preview.addEventListener('scroll', onScroll, { passive: true });
        source.addEventListener('input', invalidate);

        state.resizeObserver = new ResizeObserver(invalidate);
        state.resizeObserver.observe(source);
        state.resizeObserver.observe(preview);

        // 預覽是 Blazor 重新算繪的，內容一換位置就全變了。
        state.mutationObserver = new MutationObserver(invalidate);
        state.mutationObserver.observe(preview, { childList: true, subtree: true, characterData: true });

        scrollSyncStates.set(source, state);
        return true;
    },

    unbindScrollSync: function (source) {
        const state = source ? scrollSyncStates.get(source) : null;
        if (!state) {
            return;
        }

        cancelAnimationFrame(state.frame);
        state.source.removeEventListener('scroll', state.handlers.onScroll);
        state.preview.removeEventListener('scroll', state.handlers.onScroll);
        state.source.removeEventListener('input', state.handlers.invalidate);
        state.resizeObserver.disconnect();
        state.mutationObserver.disconnect();
        state.mirror.remove();
        scrollSyncStates.delete(source);
    }
};

const scrollSyncStates = new WeakMap();

function syncFrom(state, from) {
    const to = from === state.source ? state.preview : state.source;
    const fromKey = from === state.source ? 'source' : 'preview';
    const toKey = fromKey === 'source' ? 'preview' : 'source';

    const fromMax = from.scrollHeight - from.clientHeight;
    const toMax = to.scrollHeight - to.clientHeight;
    if (toMax <= 0) {
        return;
    }

    let top;
    if (fromMax > 0 && from.scrollTop >= fromMax - 1) {
        // 捲到底時另一邊也要到底——最後一段往往比視窗矮，內插永遠到不了盡頭。
        top = toMax;
    } else {
        state.pairs = state.pairs || buildPairs(state);
        top = interpolate(state.pairs, fromKey, toKey, from.scrollTop);
    }

    top = Math.max(0, Math.min(toMax, top));
    if (Math.abs(to.scrollTop - top) < 1) {
        return;
    }

    state.expected = { element: to, top: top };
    to.scrollTop = top;
}

// 對應點：[{ source: 原文像素, preview: 預覽像素 }]，兩欄都嚴格遞增。
function buildPairs(state) {
    const sourceTops = measureSourceLines(state);
    const previewRect = state.preview.getBoundingClientRect();
    const byLine = new Map();

    state.preview.querySelectorAll('[data-source-line]').forEach(element => {
        const line = parseInt(element.getAttribute('data-source-line'), 10);
        if (isNaN(line) || line >= sourceTops.length) {
            return;
        }

        // 巢狀區塊（清單與它的第一個項目）會落在同一行，取最上面那一個。
        const top = element.getBoundingClientRect().top - previewRect.top + state.preview.scrollTop;
        if (!byLine.has(line) || top < byLine.get(line)) {
            byLine.set(line, top);
        }
    });

    const pairs = [{ source: 0, preview: 0 }];
    [...byLine.keys()].sort((a, b) => a - b).forEach(line => {
        const last = pairs[pairs.length - 1];
        const pair = { source: sourceTops[line], preview: byLine.get(line) };
        // 丟掉會讓對應倒退的點（例如表格內的列比表格本身還早被量到），內插才不會反向。
        if (pair.source > last.source && pair.preview > last.preview) {
            pairs.push(pair);
        }
    });

    const end = { source: state.source.scrollHeight, preview: state.preview.scrollHeight };
    const last = pairs[pairs.length - 1];
    if (end.source > last.source && end.preview > last.preview) {
        pairs.push(end);
    }

    return pairs;
}

// <textarea> 沒有「第 n 行在第幾個像素」的 API，而且會自動換行（一行原文可能佔好幾個視覺行）。
// 用一個照抄字型、寬度與內距的隱藏 div，每行原文放一個子元素，量它的位置。
function measureSourceLines(state) {
    const source = state.source;
    const mirror = state.mirror;
    const style = getComputedStyle(source);

    ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'tabSize',
        'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft'].forEach(name => {
        mirror.style[name] = style[name];
    });
    // clientWidth 含內距、不含捲軸與框線，正好是 textarea 實際排字的寬度。
    mirror.style.width = source.clientWidth + 'px';

    const fragment = document.createDocumentFragment();
    source.value.split('\n').forEach(text => {
        const line = document.createElement('div');
        // 空行沒有內容就沒有高度，放一個零寬字元撐住。
        line.textContent = text.length > 0 ? text : '​';
        fragment.appendChild(line);
    });
    mirror.replaceChildren(fragment);

    return Array.from(mirror.children, child => child.offsetTop);
}

function interpolate(pairs, fromKey, toKey, value) {
    for (let i = 0; i < pairs.length - 1; i++) {
        const a = pairs[i];
        const b = pairs[i + 1];
        if (value < b[fromKey]) {
            const ratio = (value - a[fromKey]) / (b[fromKey] - a[fromKey]);
            return a[toKey] + ratio * (b[toKey] - a[toKey]);
        }
    }

    return pairs[pairs.length - 1][toKey];
}

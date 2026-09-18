// <textarea> 裡的選取與捲動。
//
// 這裡刻意只做「DOM 才做得到」的三件事：選取、聚焦、捲動。找字、算索引、取代
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
    }
};

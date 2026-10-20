// 問答區自動捲到最下方（1.0.50）：AI 問答視窗與使用說明「問 AI 助理」共用。
//
// - follow(el)：掛一次就好（重複呼叫會被旗標擋掉）。第一次掛上時捲到底；之後內容變動（新訊息、
//   回答串流逐字長出來）時，只要使用者原本就停在最下方附近，就跟著捲到底。
// - toBottom(el)：強制捲到底並恢復「跟隨」。送出問題、切換對話時呼叫。
//
// 使用者往上捲去看舊訊息時就不再跟隨，不會一直把畫面拉回最下面；捲回底部附近又會恢復。
(function () {
    // 距離底部多少 px 以內算「停在最下方」。
    const nearBottomPx = 80;

    function scrollToEnd(el) {
        el.scrollTop = el.scrollHeight;
    }

    function isNearBottom(el) {
        return el.scrollHeight - el.scrollTop - el.clientHeight <= nearBottomPx;
    }

    window.meetingRecordChatScroll = {
        follow: function (el) {
            if (!el || el.__meetingRecordChatScroll) {
                return;
            }

            el.__meetingRecordChatScroll = true;
            el.__meetingRecordChatStick = true;

            el.addEventListener('scroll', function () {
                el.__meetingRecordChatStick = isNearBottom(el);
            });

            new MutationObserver(function () {
                if (el.__meetingRecordChatStick) {
                    scrollToEnd(el);
                }
            }).observe(el, { childList: true, subtree: true, characterData: true });

            scrollToEnd(el);
        },

        toBottom: function (el) {
            if (!el) {
                return;
            }

            el.__meetingRecordChatStick = true;
            scrollToEnd(el);
        },
    };
})();

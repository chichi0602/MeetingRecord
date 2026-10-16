// 滑鼠移上去時的提示（1.0.38），搭配 Components/Commons/HoverTip.razor。
//
// 全部在瀏覽器端處理、不經 Blazor 連線：AntDesign <Tooltip> 的顯示／隱藏要跟伺服器來回，
// 連到遠端主機有延遲時「離開」事件會漏掉，提示框就卡在畫面上。
//
// 整頁只有一個泡泡，掛在 body 下、position: fixed——不會被表格的捲動容器或固定欄裁掉。
(function () {
    'use strict';

    const GAP = 8;
    const MARGIN = 8;

    let bubble = null;
    let currentHost = null;
    let watching = false;

    function ensureBubble() {
        if (bubble === null) {
            bubble = document.createElement('div');
            bubble.className = 'hover-tip-bubble';
            bubble.setAttribute('role', 'tooltip');
            document.body.appendChild(bubble);
        }
        return bubble;
    }

    function hide() {
        currentHost = null;
        if (bubble !== null) {
            bubble.classList.remove('hover-tip-bubble-visible');
        }
    }

    function position(host) {
        const rect = host.getBoundingClientRect();
        const width = bubble.offsetWidth;
        const height = bubble.offsetHeight;

        // 預設放上方；上方空間不夠就放下方。
        let top = rect.top - height - GAP;
        if (top < MARGIN) {
            top = rect.bottom + GAP;
        }

        let left = rect.left + rect.width / 2 - width / 2;
        left = Math.max(MARGIN, Math.min(left, window.innerWidth - width - MARGIN));

        bubble.style.top = `${Math.round(top)}px`;
        bubble.style.left = `${Math.round(left)}px`;
    }

    // Blazor 重繪可能把滑鼠下的元素整個拿掉（例如上傳結束整列換掉），那時不會有 pointerout，要自己發現。
    function watchHost() {
        if (watching) {
            return;
        }
        watching = true;

        const tick = () => {
            if (currentHost === null) {
                watching = false;
                return;
            }
            if (!currentHost.isConnected || !currentHost.hasAttribute('data-hover-tip')) {
                hide();
                watching = false;
                return;
            }
            requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
    }

    function show(host) {
        // 每次都讀當下的值：「轉錄中（第 n/m 段）」這類提示會隨進度改變。
        const text = host.getAttribute('data-hover-tip');
        if (!text) {
            hide();
            return;
        }

        const el = ensureBubble();
        el.textContent = text;
        currentHost = host;
        el.classList.add('hover-tip-bubble-visible');
        position(host);
        watchHost();
    }

    function hostOf(target) {
        return target instanceof Element ? target.closest('[data-hover-tip]') : null;
    }

    document.addEventListener('pointerover', (e) => {
        if (e.pointerType === 'touch') {
            return;
        }
        const host = hostOf(e.target);
        if (host !== null && host !== currentHost) {
            show(host);
        } else if (host === null && currentHost !== null) {
            hide();
        }
    });

    document.addEventListener('pointerout', (e) => {
        if (currentHost === null) {
            return;
        }
        // 只在真的離開宿主時才收；在宿主內部的子元素之間移動不算。
        const next = e.relatedTarget;
        if (!(next instanceof Node) || !currentHost.contains(next)) {
            hide();
        }
    });

    document.addEventListener('focusin', (e) => {
        const host = hostOf(e.target);
        if (host !== null) {
            show(host);
        }
    });

    document.addEventListener('focusout', hide);
    document.addEventListener('pointerdown', hide);
    document.addEventListener('scroll', hide, true);
    window.addEventListener('blur', hide);
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            hide();
        }
    });
})();

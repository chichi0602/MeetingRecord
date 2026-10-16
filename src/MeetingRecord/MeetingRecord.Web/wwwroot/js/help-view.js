// 使用說明頁（1.0.42）：/ 快速搜尋、關鍵字高亮、跨分類的錨點連結。
//
// 只做「DOM 才做得到」的事；切分類、搜尋的邏輯都在 C# 的 HelpManual（可單元測試）。
window.meetingRecordHelpView = (function () {
    const registry = new WeakMap();

    function isTyping(target) {
        if (!(target instanceof Element)) {
            return false;
        }
        const tag = target.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable;
    }

    function findSearchInput(root) {
        return root.querySelector('.help-view-search input') || root.querySelector('.help-view-search');
    }

    function attach(root, dotNet) {
        if (!root || registry.has(root)) {
            return;
        }

        // 「/」快速搜尋（焦點不在輸入框時）；在搜尋框按 Esc 清空。
        const onKeyDown = (e) => {
            if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isTyping(e.target)) {
                const input = findSearchInput(root);
                if (input) {
                    e.preventDefault();
                    input.focus();
                    if (typeof input.select === 'function') {
                        input.select();
                    }
                }
            } else if (e.key === 'Escape' && root.contains(e.target) && isTyping(e.target)) {
                dotNet.invokeMethodAsync('ClearSearchFromJs');
            }
        };

        // 說明內容裡的 #錨點：目標可能在別的分類，交給 C# 先切分類，再捲過去。
        const onClick = (e) => {
            const link = e.target instanceof Element ? e.target.closest('a[href^="#"]') : null;
            if (!link || !root.contains(link)) {
                return;
            }
            const id = decodeURIComponent(link.getAttribute('href').slice(1));
            if (!id) {
                return;
            }
            e.preventDefault();
            dotNet.invokeMethodAsync('JumpToAnchorFromJs', id);
        };

        document.addEventListener('keydown', onKeyDown);
        root.addEventListener('click', onClick);
        registry.set(root, { onKeyDown, onClick });
    }

    function detach(root) {
        const handlers = root ? registry.get(root) : null;
        if (!handlers) {
            return;
        }
        document.removeEventListener('keydown', handlers.onKeyDown);
        root.removeEventListener('click', handlers.onClick);
        registry.delete(root);
    }

    // 只在文字節點包 <mark>，不碰任何 HTML 屬性——直接在 HTML 字串上取代會把 href、id 一起改壞。
    function highlight(root, terms) {
        if (!root) {
            return;
        }

        root.querySelectorAll('mark.help-view-mark').forEach((mark) => {
            mark.replaceWith(document.createTextNode(mark.textContent));
        });
        root.normalize();

        const words = (terms || []).filter((t) => t && t.trim().length > 0);
        if (words.length === 0) {
            return;
        }

        const escaped = words.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
        const pattern = new RegExp(`(${escaped.join('|')})`, 'gi');
        const scope = root.querySelector('.help-view-results') || root;

        const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
        const targets = [];
        while (walker.nextNode()) {
            const node = walker.currentNode;
            pattern.lastIndex = 0;
            if (node.nodeValue && pattern.test(node.nodeValue)) {
                targets.push(node);
            }
        }

        targets.forEach((node) => {
            const fragment = document.createDocumentFragment();
            const parts = node.nodeValue.split(pattern);
            parts.forEach((part, index) => {
                if (index % 2 === 1) {
                    const mark = document.createElement('mark');
                    mark.className = 'help-view-mark';
                    mark.textContent = part;
                    fragment.appendChild(mark);
                } else if (part.length > 0) {
                    fragment.appendChild(document.createTextNode(part));
                }
            });
            node.replaceWith(fragment);
        });
    }

    function scrollTo(root, id) {
        const target = id ? document.getElementById(id) : null;
        if (target && root && root.contains(target)) {
            target.scrollIntoView({ behavior: 'smooth', block: 'start' });
            return;
        }

        // 切換分類（1.0.43）：只有說明區頂端已經捲出畫面上方時才捲回來。
        // 1.0.42 不管位置一律 scrollIntoView，頂端明明在畫面上也會被對齊到視窗最上緣，看起來就是「往上跳一下」。
        const content = root ? (root.querySelector('.help-view-body') || root) : null;
        if (content && content.getBoundingClientRect().top < 0) {
            content.scrollIntoView({ behavior: 'auto', block: 'start' });
        }
    }

    return { attach, detach, highlight, scrollTo };
})();

document.querySelectorAll('[data-copy]').forEach(button => {
    button.addEventListener('click', async () => {
        const status = button.nextElementSibling;
        try {
            await navigator.clipboard.writeText(document.getElementById(button.dataset.copy).textContent);
            status.textContent = '已复制';
        } catch {
            status.textContent = '无法自动复制，请选中内容手动复制。';
        }
    });
});

// 浏览器返回缓存恢复页面时隐藏一次性凭据，避免恢复旧展示状态；恢复的页面上表单不再算「正在提交」，可以再按。
window.addEventListener('pageshow', event => {
    if (event.persisted && document.querySelector('[data-sensitive]')) location.replace('/Users');
    if (event.persisted) document.querySelectorAll('form[data-submitting]').forEach(form => delete form.dataset.submitting);
});

// 导出成功的页面打开时自动下载一次；读屏先读出结果，下载链接留在页面上，没自动下载时可以手动按。
document.querySelectorAll('[data-auto-download]').forEach(link => link.click());

// 原生表单提交时给键盘和读屏使用者反馈，检测期间不保存秘密输入副本。
// 同一张表单提交出去以后、页面回来以前再按一次会被拦下（P-112），读出「正在处理」而不是重复提交。
// 隐藏的表单（上游菜单里的按钮借它提交）把等待提示放在它所在的那一家里。
function submitFeedback(root) {
    root.querySelectorAll('form[method="post"]').forEach(form => {
        if (form.dataset.feedbackReady) return; form.dataset.feedbackReady = 'true';
        form.addEventListener('submit', event => {
            if (form.dataset.submitting) { event.preventDefault(); return; }
            form.dataset.submitting = 'true';
            const buttons = [...form.querySelectorAll('button[type="submit"]'), ...(form.id ? document.querySelectorAll(`button[form="${CSS.escape(form.id)}"]`) : [])];
            buttons.forEach(button => button.setAttribute('aria-busy', 'true'));
            const host = form.hidden ? form.parentElement : form;
            let progress = host.querySelector(':scope > [data-submit-status]');
            if (!progress) {
                progress = document.createElement('p'); progress.dataset.submitStatus = '';
                progress.setAttribute('role', 'status'); host.append(progress);
            }
            const action = event.submitter?.formAction || form.action;
            progress.textContent = form.hasAttribute('data-device-maintenance') ? '正在等待已有官方调用结束，设备更换尚未完成，请稍候。' : action.includes('handler=Test') ? '正在建立检测，请稍候…'
                : action.includes('handler=Relogin') ? '正在打开浏览器登录页，请在浏览器里登录原来的那个账户；登录完这一页会读出结果。' : '正在处理，请稍候…';
        });
    });
}
submitFeedback(document);

// 菜单按钮（WAI-ARIA APG Menu Button）：按钮 aria-haspopup="menu"、aria-controls 指向 role="menu"，菜单项 role="menuitem"。
// 空格、回车、下箭头、应用键、Shift+F10、右键打开并把焦点放到第一项（上箭头放到最后一项）；菜单里上下键移动、Home、End 到头尾，
// 回车或空格执行，Esc 关闭并把焦点还给按钮，Tab 关闭菜单、焦点照常往后走。任何页面的菜单都用这一份。
function setupMenus(root) {
    root.querySelectorAll('[aria-haspopup="menu"][aria-controls]').forEach(button => {
        const menu = document.getElementById(button.getAttribute('aria-controls'));
        if (!menu || button.dataset.menuReady) return; button.dataset.menuReady = 'true';
        const items = () => [...menu.querySelectorAll('[role="menuitem"]')];
        const open = last => {
            document.querySelectorAll('[aria-haspopup="menu"][aria-expanded="true"]').forEach(other => { if (other !== button) other.dispatchEvent(new CustomEvent('menu-close')); });
            menu.hidden = false; button.setAttribute('aria-expanded', 'true');
            const list = items(); (last ? list[list.length - 1] : list[0])?.focus();
        };
        const close = giveBack => {
            menu.hidden = true; button.setAttribute('aria-expanded', 'false');
            if (giveBack) button.focus();
        };
        button.addEventListener('menu-close', () => close(false));
        button.addEventListener('click', () => menu.hidden ? open(false) : close(true));
        button.addEventListener('keydown', event => {
            if (event.key === 'ArrowDown' || event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) { event.preventDefault(); open(false); }
            else if (event.key === 'ArrowUp') { event.preventDefault(); open(true); }
        });
        button.addEventListener('contextmenu', event => { event.preventDefault(); open(false); });
        menu.addEventListener('keydown', event => {
            const list = items(), index = list.indexOf(document.activeElement);
            if (event.key === 'ArrowDown') { event.preventDefault(); list[(index + 1) % list.length].focus(); }
            else if (event.key === 'ArrowUp') { event.preventDefault(); list[(index - 1 + list.length) % list.length].focus(); }
            else if (event.key === 'Home') { event.preventDefault(); list[0].focus(); }
            else if (event.key === 'End') { event.preventDefault(); list[list.length - 1].focus(); }
            else if (event.key === 'Escape') { event.preventDefault(); close(true); }
            // 焦点先回到按钮再交给浏览器的 Tab：焦点落到按钮后面的下一处，不会停在已经收起的菜单里。
            else if (event.key === 'Tab') close(true);
            // 链接本身不认空格，这里替它执行；按钮的空格由浏览器自己执行。
            else if (event.key === ' ' && document.activeElement?.tagName === 'A') { event.preventDefault(); document.activeElement.click(); }
            else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) event.preventDefault();
        });
        // 焦点离开菜单和按钮（点了别处）就收起菜单。
        menu.addEventListener('focusout', event => { if (!menu.contains(event.relatedTarget) && event.relatedTarget !== button) close(false); });
    });
}
setupMenus(document);

// 回发后焦点落在哪（P-105、P-108、P-124、P-125）：先去第一个出错的输入框，其次错误摘要，再次页面点名的那一项（刚处理的那一家、那一人），
// 最后去结果提示。落到点名的那一项时，让它同时读出结果提示（aria-describedby），离开时还原。没有结果的普通打开不挪焦点。
function initialFocus() {
    const named = document.querySelector('[data-focus-id]');
    const target = named ? document.getElementById(named.dataset.focusId) : null;
    const notice = document.getElementById('notice');
    const chosen = document.querySelector('main [aria-invalid="true"]') ?? document.querySelector('main div.validation-summary-errors') ?? target ?? notice;
    if (!chosen) return;
    if (chosen === target && notice) {
        const original = chosen.getAttribute('aria-describedby');
        chosen.setAttribute('aria-describedby', original ? 'notice ' + original : 'notice');
        chosen.addEventListener('blur', () => original ? chosen.setAttribute('aria-describedby', original) : chosen.removeAttribute('aria-describedby'), { once: true });
    }
    if (!chosen.hasAttribute('tabindex') && !chosen.matches('a[href], button, input, select, textarea')) chosen.tabIndex = -1;
    chosen.focus();
}
initialFocus();

// 上游页的后台检测：状态区每秒问一次检测状态，不整页刷新、不挪焦点。结束时只把结论放进状态区（读屏只读这一次），
// 再把「上游列表」一节换成最新的（不是朗读区，换的时候不出声），换进来的表单和菜单重新挂上；焦点原来在这一节里的，换完回到同一个元素上。
const checkStatus = document.getElementById('check-status');
if (checkStatus) {
    const id = encodeURIComponent(checkStatus.dataset.checkId), candidate = encodeURIComponent(checkStatus.dataset.candidateId);
    const refresh = async () => {
        try {
            const page = await fetch(`/Upstream?candidateId=${candidate}`, { cache: 'no-store' });
            const html = new DOMParser().parseFromString(await page.text(), 'text/html');
            const fresh = html.getElementById('saved'), old = document.getElementById('saved');
            if (!fresh || !old) return;
            const focused = old.contains(document.activeElement) ? document.activeElement.id : null;
            old.replaceWith(fresh); submitFeedback(fresh); setupMenus(fresh);
            if (focused) document.getElementById(focused)?.focus();
        } catch { /* 换不了就留着旧的一节，结论已经读出；管理员重新打开页面即可看到最新列表。 */ }
    };
    const poll = async () => {
        let state;
        try {
            const response = await fetch(`/Upstream?handler=Check&checkId=${id}`, { headers: { Accept: 'application/json' }, cache: 'no-store' });
            if (!response.ok) { checkStatus.textContent = '这次检测的记录已经不在了（服务可能重启过），请重新打开上游页查看结果。'; return; }
            state = await response.json();
        } catch { setTimeout(poll, 2000); return; }
        if (!state.ended) { setTimeout(poll, 1000); return; }
        document.getElementById('check-cancel')?.remove();
        checkStatus.textContent = state.message;
        await refresh();
    };
    setTimeout(poll, 1000);
}

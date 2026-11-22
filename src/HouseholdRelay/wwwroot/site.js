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

// 浏览器返回缓存恢复页面时隐藏一次性凭据，避免恢复旧展示状态。
window.addEventListener('pageshow', event => {
    if (event.persisted && document.querySelector('[data-sensitive]')) location.replace('/Users');
});

// 导出成功的页面打开时自动下载一次；读屏先读出结果，下载链接留在页面上，没自动下载时可以手动按。
document.querySelectorAll('[data-auto-download]').forEach(link => link.click());

// 原生表单提交时给键盘和读屏使用者反馈，检测期间不保存秘密输入副本。
function submitFeedback(root) {
    root.querySelectorAll('form[method="post"]').forEach(form => {
        form.addEventListener('submit', () => {
            form.querySelectorAll('button[type="submit"]').forEach(button => button.setAttribute('aria-busy', 'true'));
            let progress = form.querySelector('[data-submit-status]');
            if (!progress) {
                progress = document.createElement('p'); progress.dataset.submitStatus = '';
                progress.setAttribute('role', 'status'); form.append(progress);
            }
            progress.textContent = form.hasAttribute('data-device-maintenance') ? '正在等待已有官方调用结束，设备更换尚未完成，请稍候。' : form.action.includes('handler=Test') ? '正在建立检测，请稍候…' : '正在处理，请稍候…';
        });
    });
}
submitFeedback(document);

// 上游页的后台检测：状态区每秒问一次检测状态，不整页刷新、不挪焦点。结束时只把结论放进状态区（读屏只读这一次），
// 再把「选择模型并测试」和「已保存的上游」两节换成最新的（这两节不是朗读区，换的时候不出声），换进来的表单重新挂上提交反馈。
const checkStatus = document.getElementById('check-status');
if (checkStatus) {
    const id = encodeURIComponent(checkStatus.dataset.checkId), candidate = encodeURIComponent(checkStatus.dataset.candidateId);
    const refresh = async () => {
        try {
            const page = await fetch(`/Upstream?candidateId=${candidate}`, { cache: 'no-store' });
            const html = new DOMParser().parseFromString(await page.text(), 'text/html');
            for (const name of ['test', 'saved']) {
                const fresh = html.getElementById(name), old = document.getElementById(name);
                if (fresh && old) { old.replaceWith(fresh); submitFeedback(fresh); }
            }
        } catch { /* 换不了就留着旧的两节，结论已经读出；管理员重新打开页面即可看到最新列表。 */ }
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

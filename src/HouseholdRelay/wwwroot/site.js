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

// 原生表单提交时给键盘和读屏使用者反馈，检测期间不保存秘密输入副本。
document.querySelectorAll('form[method="post"]').forEach(form => {
    form.addEventListener('submit', () => {
        form.querySelectorAll('button[type="submit"]').forEach(button => button.setAttribute('aria-busy', 'true'));
        let progress = form.querySelector('[data-submit-status]');
        if (!progress) {
            progress = document.createElement('p'); progress.dataset.submitStatus = '';
            progress.setAttribute('role', 'status'); form.append(progress);
        }
        progress.textContent = form.action.includes('handler=Test') ? '正在检测上游，请稍候…' : '正在处理，请稍候…';
    });
});

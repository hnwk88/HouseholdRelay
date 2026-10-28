document.querySelectorAll('[data-copy]').forEach(button => {
    button.addEventListener('click', async () => {
        const status = button.nextElementSibling;
        try {
            await navigator.clipboard.writeText(document.getElementById(button.dataset.copy).textContent);
            status.textContent = '已复制';
        } catch {
            status.textContent = '无法自动复制，请选中令牌手动复制。';
        }
    });
});

// 浏览器返回缓存恢复页面时隐藏一次性凭据，避免恢复旧展示状态。
window.addEventListener('pageshow', event => {
    if (event.persisted && document.querySelector('[data-sensitive]')) location.replace('/Users');
});

document.querySelectorAll('time[datetime]').forEach(element => {
    element.textContent = new Date(element.dateTime).toLocaleString();
});
document.querySelectorAll('[data-timezone]').forEach(input => input.value = Intl.DateTimeFormat().resolvedOptions().timeZone);
document.querySelectorAll('[data-utc]').forEach(input => {
    if (!input.dataset.utc) return;
    const date = new Date(input.dataset.utc);
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    input.value = local.toISOString().slice(0, 16);
});
document.querySelector('[data-usage-filter]')?.addEventListener('submit', event => {
    event.target.querySelectorAll('input[type="datetime-local"]').forEach(input => {
        if (!input.value) return;
        const utc = new Date(input.value).toISOString();
        input.type = 'hidden'; input.value = utc;
    });
});

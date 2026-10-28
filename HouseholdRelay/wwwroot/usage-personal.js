const form = document.getElementById('usage-form');
const results = document.getElementById('results');
const status = document.getElementById('status');
const pagination = document.getElementById('pagination');
let page = 1, active;
const kinds = { inference: '推理', estimate: '估算', models: '模型查询' };
const outcomes = { succeeded: '成功', failed: '失败', cancelled: '已取消', interrupted: '中断', started: '处理中 / 待写入' };
const completeness = { complete: '完整', partial: '部分 / 不完整', not_provided: '上游未提供', not_applicable: '不适用' };
const count = value => value == null ? '未知' : value.toLocaleString();
const moneySources = { reported: '上游报告', estimated: '估算' };
const moneyValue = value => Number(value).toLocaleString(undefined, { maximumFractionDigits: 9 });
function moneyTotals(summary, parent) {
    if (summary.kind !== 'inference') { element('p', '金额：不适用', parent); return; }
    if (!summary.money.length) element('p', '金额：未知', parent);
    for (const money of summary.money) element('p', `已知金额（${moneySources[money.source]}）：${moneyValue(money.amount)} ${money.currency} · ${money.requests} 次，其中不完整 ${money.incomplete} 次`, parent);
    element('p', `金额未知：${summary.unknownAmounts} 次。不同币种和来源分开统计。`, parent);
}
function element(tag, text, parent, className) {
    const item = document.createElement(tag); if (text != null) item.textContent = text;
    if (className) item.className = className; parent.append(item); return item;
}
function clear() {
    active?.abort(); results.replaceChildren(); pagination.hidden = true; status.textContent = '';
}
function render(report) {
    results.replaceChildren();
    if (report.persistenceFailures) element('p', '用量保存或清理曾失败，请联系管理员检查；统计可能不完整。', results, 'validation-summary-errors');
    element('p', `当前窗口从 ${new Date(report.windowStart).toLocaleString()} 起，共 ${report.total} 次请求。`, results);
    if (!report.total) element('p', '没有符合条件的请求记录。', results, 'notice');
    for (const summary of report.summary) {
        const card = element('section', null, results, 'card'); card.dataset.usageKind = summary.kind;
        element('h2', `${kinds[summary.kind]} · ${summary.requests} 次`, card);
        element('p', `成功 ${summary.succeeded} · 失败 ${summary.failed} · 取消 ${summary.cancelled} · 中断 ${summary.interrupted} · 处理中 / 待写入 ${summary.pending} · 未发送 ${summary.notSent}`, card);
        if (summary.kind === 'models') { element('p', 'Token：不适用', card); continue; }
        element('p', `已知输入：${count(summary.tokens.input)}` + (summary.kind === 'inference' ? ` · 已知输出：${count(summary.tokens.output)}` : ''), card);
        if (summary.kind === 'inference') element('p', `缓存读取：${count(summary.tokens.cacheRead)} · 缓存写入：${count(summary.tokens.cacheWrite)}`, card);
        element('p', `缺失 / 不完整：${summary.incomplete} 次。缓存列独立显示，不与普通输入相加。`, card);
        moneyTotals(summary, card);
    }
    if (report.periods.length) {
        element('h2', report.granularity === 'hour' ? '按小时统计' : '按天统计', results);
        element('p', `统计时区：${report.timeZone}；每个时段按请求开始时间归属。`, results);
        const region = element('div', null, results, 'table-scroll'); region.tabIndex = 0; region.setAttribute('role', 'region'); region.setAttribute('aria-label', '按时间统计，可横向滚动');
        const table = element('table', null, region), head = element('tr', null, element('thead', null, table));
        for (const title of ['时段开始', '类型', '请求 / 成功', '失败 / 取消 / 中断', '已知输入 / 输出', '用量不完整', '金额']) element('th', title, head);
        const body = element('tbody', null, table);
        for (const period of report.periods) {
            const item = period.summary, tr = element('tr', null, body);
            element('td', new Date(period.start).toLocaleString(undefined, { timeZone: report.timeZone, timeZoneName: 'short' }), tr);
            element('td', kinds[item.kind], tr); element('td', `${item.requests} / ${item.succeeded}`, tr);
            element('td', `${item.failed} / ${item.cancelled} / ${item.interrupted}`, tr);
            element('td', item.kind === 'models' ? '不适用' : `${count(item.tokens.input)} / ${item.kind === 'estimate' ? '不适用' : count(item.tokens.output)}`, tr);
            element('td', `${item.incomplete} 次`, tr); moneyTotals(item, element('td', null, tr));
        }
    }
    if (report.rows.length) {
        element('h2', '请求明细', results);
        const region = element('div', null, results, 'table-scroll'); region.tabIndex = 0; region.setAttribute('role', 'region'); region.setAttribute('aria-label', '用量明细，可横向滚动');
        const table = element('table', null, region), head = element('tr', null, element('thead', null, table));
        for (const title of ['时间 / 请求', '使用者 / 模型', '接口 / 结果', '发送 / 状态', '输入 / 输出', '缓存读 / 写', '金额', '完整性']) element('th', title, head);
        const body = element('tbody', null, table);
        for (const row of report.rows) {
            const tr = element('tr', null, body), cell = element('td', new Date(row.startedAt).toLocaleString(), tr);
            element('small', row.requestId, cell);
            const owner = element('td', row.displayName, tr); element('small', row.model || '模型未知', owner);
            const kind = element('td', kinds[row.kind], tr); element('small', outcomes[row.outcome], kind);
            const sent = element('td', row.dispatched ? '已尝试发送' : '未发送', tr); element('small', `中转 ${row.statusCode ?? '—'} / 上游 ${row.upstreamStatus ?? '—'}`, sent);
            element('td', row.completeness === 'not_applicable' ? '不适用' : `${count(row.tokens.input)} / ${row.kind === 'estimate' ? '不适用' : count(row.tokens.output)}`, tr);
            element('td', row.kind === 'inference' && row.dispatched ? `${count(row.tokens.cacheRead)} / ${count(row.tokens.cacheWrite)}` : '不适用', tr);
            element('td', row.money.source === 'not_applicable' ? '不适用' : row.money.amount == null ? '未知' : `${moneyValue(row.money.amount)} ${row.money.currency} · ${moneySources[row.money.source]}${row.money.completeness === 'partial' ? '（部分）' : ''}`, tr);
            element('td', completeness[row.completeness], tr);
        }
    }
    pagination.hidden = report.total === 0;
    document.getElementById('page-label').textContent = `第 ${report.page} 页 · 每页 ${report.pageSize} 条`;
    document.getElementById('previous').disabled = report.page <= 1;
    document.getElementById('next').disabled = report.page * report.pageSize >= report.total;
}
async function query(target) {
    clear(); const controller = active = new AbortController(); status.textContent = '正在查询…';
    const parameters = new URLSearchParams({ page: String(target), timezone: Intl.DateTimeFormat().resolvedOptions().timeZone, granularity: document.getElementById('granularity').value });
    for (const key of ['from', 'until', 'kind', 'model', 'outcome']) {
        let value = document.getElementById(key).value;
        if (value && (key === 'from' || key === 'until')) value = new Date(value).toISOString();
        if (value) parameters.set(key, value);
    }
    try {
        const response = await fetch('/my-usage/data?' + parameters, { headers: { Authorization: 'Bearer ' + document.getElementById('token').value }, cache: 'no-store', credentials: 'omit', signal: controller.signal });
        if (!response.ok) throw new Error(response.status === 401 ? '令牌无效或已停用，请检查个人令牌。' : response.status === 400 ? '查询条件无效，请检查时间和筛选条件。' : '用量暂时无法查询，请稍后重试或联系管理员。');
        const report = await response.json();
        if (active !== controller) return;
        page = target; render(report); status.textContent = '查询完成。';
    } catch (error) { if (error.name !== 'AbortError' && active === controller) status.textContent = error.message; }
}
form.addEventListener('submit', event => { event.preventDefault(); query(1); });
document.getElementById('previous').addEventListener('click', () => query(page - 1));
document.getElementById('next').addEventListener('click', () => query(page + 1));
document.getElementById('forget').addEventListener('click', () => { clear(); document.getElementById('token').value = ''; });
window.addEventListener('pagehide', () => { clear(); document.getElementById('token').value = ''; });

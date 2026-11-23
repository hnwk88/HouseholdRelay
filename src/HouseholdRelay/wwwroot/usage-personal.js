const form = document.getElementById('usage-form');
const results = document.getElementById('results');
const status = document.getElementById('status');
const pagination = document.getElementById('pagination');
const previous = document.getElementById('previous'), next = document.getElementById('next'), pageLabel = document.getElementById('page-label');
// 已提交的条件（令牌与筛选）：翻页只换页码，不重读表单里还没提交的改动；按「查询我的用量」时才换成新的。
let committed = null, page = 1, active;
// 类别、结果、完整性、金额来源、币种的中文名都取自服务端 UsageLabels 那一份（/my-usage/labels 与报告里的 labels），这里不另抄。
let labels = { kinds: {}, outcomes: {}, completeness: {}, moneySources: {}, currencies: {} };
// 不带单位的计数（表格格子用，单位在表头）；带单位的读法（读句用）。
const count = value => value == null ? '未知' : value.toLocaleString();
const tokens = value => value == null ? '未知' : `${value.toLocaleString()} 个 token`;
const times = value => `${value.toLocaleString()} 次`;
const amount = (value, currency) => `${Number(value).toLocaleString(undefined, { maximumFractionDigits: 9 })} ${labels.currencies[currency] ?? currency}`;

// 打开页面时按标签表填「接口类别」「结果」的选项，与管理页同一份中文名、同一顺序；取不到时只留「全部」，查询照样能用。
async function fillOptions() {
    try {
        const response = await fetch('/my-usage/labels', { cache: 'no-store', credentials: 'omit' });
        if (!response.ok) return;
        labels = await response.json();
        for (const [id, table] of [['kind', labels.kinds], ['outcome', labels.outcomes]]) {
            const select = document.getElementById(id);
            for (const [value, text] of Object.entries(table)) { const option = document.createElement('option'); option.value = value; option.textContent = text; select.append(option); }
        }
    } catch { /* 取不到标签表时只留「全部」，不影响查询。 */ }
}
fillOptions();

function moneyTotals(summary, parent) {
    if (summary.kind !== 'inference') { element('p', '金额：不适用', parent); return; }
    if (!summary.money.length) element('p', '金额：未知', parent);
    for (const money of summary.money) element('p', `已知金额（${labels.moneySources[money.source]}）${amount(money.amount, money.currency)}，请求 ${times(money.requests)}，其中不完整 ${times(money.incomplete)}`, parent);
    element('p', `金额未知 ${times(summary.unknownAmounts)}。不同币种和来源分开统计。`, parent);
}
function element(tag, text, parent, className) {
    const item = document.createElement(tag); if (text != null) item.textContent = text;
    if (className) item.className = className; parent.append(item); return item;
}
function table(parent, name, titles) {
    const region = element('div', null, parent, 'table-scroll'); region.tabIndex = 0; region.setAttribute('role', 'region'); region.setAttribute('aria-label', name);
    const grid = element('table', null, region), head = element('tr', null, element('thead', null, grid));
    for (const title of titles) element('th', title, head);
    return element('tbody', null, grid);
}
function clear() {
    active?.abort(); results.replaceChildren(); pagination.hidden = true; status.textContent = ''; committed = null;
}
function render(report) {
    results.replaceChildren(); labels = report.labels;
    const heading = element('h2', '查询结果', results); heading.id = 'results-heading'; heading.tabIndex = -1;
    if (report.persistenceFailures) element('p', '用量保存或清理曾失败，请联系管理员检查；统计可能不完整。', results, 'validation-summary-errors');
    element('p', `当前窗口从 ${new Date(report.windowStart).toLocaleString()} 起，共请求 ${times(report.total)}。`, results);
    if (!report.total) element('p', '没有符合条件的请求记录。', results, 'notice');
    for (const summary of report.summary) {
        const card = element('section', null, results, 'card'); card.dataset.usageKind = summary.kind;
        element('h3', `${labels.kinds[summary.kind]}：请求 ${times(summary.requests)}`, card);
        element('p', `成功 ${times(summary.succeeded)}，失败 ${times(summary.failed)}，取消 ${times(summary.cancelled)}，中断 ${times(summary.interrupted)}，处理中 / 待写入 ${times(summary.pending)}，未发送 ${times(summary.notSent)}`, card);
        if (summary.kind === 'models') { element('p', 'Token：不适用', card); continue; }
        element('p', `已知输入 ${tokens(summary.tokens.input)}` + (summary.kind === 'inference' ? `，已知输出 ${tokens(summary.tokens.output)}` : ''), card);
        if (summary.kind === 'inference') element('p', `缓存读取 ${tokens(summary.tokens.cacheRead)}，缓存写入 ${tokens(summary.tokens.cacheWrite)}`, card);
        element('p', `缺失 / 不完整 ${times(summary.incomplete)}。缓存列独立显示，不与普通输入相加。`, card);
        moneyTotals(summary, card);
    }
    if (report.periods.length) {
        element('h3', report.granularity === 'hour' ? '按小时统计' : '按天统计', results);
        element('p', `统计时区：${report.timeZone}；每个时段按请求开始时间归属。`, results);
        const body = table(results, '按时间统计', ['时段开始', '类型', '请求 / 成功（次）', '失败 / 取消 / 中断（次）', '已知输入 / 输出（token）', '缓存读 / 写（token）', '用量不完整（次）', '金额']);
        for (const period of report.periods) {
            const item = period.summary, tr = element('tr', null, body);
            element('td', new Date(period.start).toLocaleString(undefined, { timeZone: report.timeZone, timeZoneName: 'short' }), tr);
            element('td', labels.kinds[item.kind], tr); element('td', `${count(item.requests)} / ${count(item.succeeded)}`, tr);
            element('td', `${count(item.failed)} / ${count(item.cancelled)} / ${count(item.interrupted)}`, tr);
            element('td', item.kind === 'models' ? '不适用' : `${count(item.tokens.input)} / ${item.kind === 'estimate' ? '不适用' : count(item.tokens.output)}`, tr);
            element('td', item.kind === 'inference' ? `${count(item.tokens.cacheRead)} / ${count(item.tokens.cacheWrite)}` : '不适用', tr);
            element('td', count(item.incomplete), tr); moneyTotals(item, element('td', null, tr));
        }
    }
    if (report.rows.length) {
        element('h3', '请求明细', results);
        const body = table(results, '用量明细', ['时间', '使用者 / 模型', '接口 / 结果', '发送 / 状态码', '输入 / 输出（token）', '缓存读 / 写（token）', '金额', '完整性', '请求编号']);
        for (const row of report.rows) {
            const tr = element('tr', null, body);
            element('td', new Date(row.startedAt).toLocaleString(), tr);
            const owner = element('td', row.displayName, tr); element('small', row.model || '模型未知', owner);
            const kind = element('td', labels.kinds[row.kind], tr); element('small', labels.outcomes[row.outcome], kind);
            const sent = element('td', row.dispatched ? '已尝试发送' : '未发送', tr); element('small', `中转 ${row.statusCode ?? '—'} / 上游 ${row.upstreamStatus ?? '—'}`, sent);
            element('td', row.completeness === 'not_applicable' ? '不适用' : `${count(row.tokens.input)} / ${row.kind === 'estimate' ? '不适用' : count(row.tokens.output)}`, tr);
            element('td', row.kind === 'inference' && row.dispatched ? `${count(row.tokens.cacheRead)} / ${count(row.tokens.cacheWrite)}` : '不适用', tr);
            element('td', row.money.source === 'not_applicable' ? '不适用' : row.money.amount == null ? '未知' : `${amount(row.money.amount, row.money.currency)} · ${labels.moneySources[row.money.source]}${row.money.completeness === 'partial' ? '（部分）' : ''}`, tr);
            element('td', labels.completeness[row.completeness], tr);
            element('td', null, tr).append(Object.assign(document.createElement('small'), { textContent: row.requestId }));
        }
    }
    const pages = Math.max(Math.ceil(report.total / report.pageSize), 1);
    pagination.hidden = report.total === 0;
    pageLabel.textContent = `第 ${report.page} 页，共 ${pages} 页，每页 ${report.pageSize} 条`;
    // 到头的按钮隐藏（读屏不会停在一个按不动的按钮上）。
    previous.hidden = !report.hasPrevious; next.hidden = !report.hasNext;
}
// 这一页明细是第几到第几条，读句用。
function range(report) {
    if (!report.total) return '没有符合条件的请求记录。';
    const first = (report.page - 1) * report.pageSize + 1, last = Math.min(report.page * report.pageSize, report.total);
    return `共 ${report.total.toLocaleString()} 条请求记录，这是第 ${report.page} 页，第 ${first} 到 ${last} 条。`;
}
async function query(target, paging) {
    active?.abort(); const controller = active = new AbortController(); status.textContent = '正在查询…';
    // 翻页时分页条留着、焦点留在按下的按钮上；重新查询才清掉旧结果。
    if (!paging) { results.replaceChildren(); pagination.hidden = true; }
    const parameters = new URLSearchParams(committed.parameters); parameters.set('page', String(target));
    try {
        const response = await fetch('/my-usage/data?' + parameters, { headers: { Authorization: 'Bearer ' + committed.token }, cache: 'no-store', credentials: 'omit', signal: controller.signal });
        if (!response.ok) throw new Error(response.status === 401 ? '令牌无效或已停用，请检查个人令牌。' : response.status === 400 ? '查询条件无效，请检查时间和筛选条件。' : '用量暂时无法查询，请稍后重试或联系管理员。');
        const report = await response.json();
        if (active !== controller) return;
        const pressed = document.activeElement;
        page = target; render(report); status.textContent = '查询完成。' + range(report);
        if (!paging) document.getElementById('results-heading').focus();
        // 按下的翻页按钮到头被隐藏了：焦点挪到另一个翻页按钮，都没有就挪到页码。
        else if ((pressed === previous || pressed === next) && pressed.hidden) (pressed === next ? previous : next).hidden ? (pageLabel.tabIndex = -1, pageLabel.focus()) : (pressed === next ? previous : next).focus();
    } catch (error) { if (error.name !== 'AbortError' && active === controller) status.textContent = error.message; }
}
form.addEventListener('submit', event => {
    event.preventDefault();
    const parameters = new URLSearchParams({ timezone: Intl.DateTimeFormat().resolvedOptions().timeZone, granularity: document.getElementById('granularity').value });
    for (const key of ['from', 'until', 'kind', 'model', 'outcome']) {
        let value = document.getElementById(key).value;
        if (value && (key === 'from' || key === 'until')) value = new Date(value).toISOString();
        if (value) parameters.set(key, value);
    }
    committed = { parameters: parameters.toString(), token: document.getElementById('token').value };
    query(1, false);
});
previous.addEventListener('click', () => { if (committed) query(page - 1, true); });
next.addEventListener('click', () => { if (committed) query(page + 1, true); });
document.getElementById('forget').addEventListener('click', () => { clear(); document.getElementById('token').value = ''; status.textContent = '已清除令牌与结果。'; });
window.addEventListener('pagehide', () => { clear(); document.getElementById('token').value = ''; });

// 用真实浏览器与发布程序验证页面；上游仅为本机可控服务，所有凭据均为测试生成。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const net = require('node:net');
const assert = require('node:assert/strict');
const dll = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const password = 'Usage-browser-fixture-password-42', upstreamSecret = 'usage-browser-fixture-upstream';
let child, browser, temporary, upstream, port, url, a, b;
const steps = [], errors = [], pending = new Set();
async function freePort() {
    const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
    const value = socket.address().port; await new Promise(resolve => socket.close(resolve)); return value;
}
async function start() {
    child = spawn('dotnet', [dll, 'serve', '--home', temporary, '--admin-port', String(port)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    try { os.setPriority(child.pid, os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { }
    let startup = ''; child.stdout.on('data', value => { startup += value.toString(); }); child.stderr.resume();
    for (let attempt = 0; attempt < 150; attempt++) {
        assert.equal(child.exitCode, null, '临时服务启动失败');
        try { if ((await fetch(url + '/healthz')).ok && startup.includes('本机管理')) return startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1]; } catch { }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('临时服务未就绪');
}
async function stop() {
    if (!child || child.exitCode !== null || child.signalCode !== null) return;
    const stopped = new Promise(resolve => child.once('exit', resolve)); child.kill(); await stopped;
}
async function personal(token, query = '') {
    const response = await fetch(url + '/my-usage/data' + query, { headers: { Authorization: 'Bearer ' + token } });
    assert.equal(response.status, 200); return response.json();
}
async function settled(token, count) {
    for (let attempt = 0; attempt < 80; attempt++) {
        const report = await personal(token);
        if (report.total === count && report.summary.every(item => item.pending === 0)) return report;
        await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error('用量记录未在期限内完成');
}
async function call(token, scenario, route = '/v1/messages') {
    const response = await fetch(url + route + '?case=' + scenario, {
        method: route === '/v1/models' ? 'GET' : 'POST', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
        body: route === '/v1/models' ? undefined : JSON.stringify({ model: 'fixture-model', stream: scenario.startsWith('sse') || scenario === 'crash', messages: [{ role: 'user', content: 'fixture-private-prompt' }], metadata: { user_id: 'forged-other-owner' } })
    });
    assert.equal(response.status, 200); if (scenario === 'crash') return response;
    await response.text(); return response;
}
async function shot(page, name) {
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
}
// 读屏顺序：主内容区的无障碍树按页面顺序写成文本，供验收逐行核对。
async function readingOrder(page, name) {
    await fs.writeFile(path.join(output, name + '.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
}
async function login(page) {
    await page.goto(url + '/Login'); await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
}
async function createUser(page, name) {
    await page.goto(url + '/Users'); await page.getByLabel('新使用者的显示名', { exact: true }).fill(name);
    await page.getByRole('button', { name: '创建并生成令牌', exact: true }).click();
    return { token: (await page.locator('#issued-token').textContent()).trim(), id: await page.locator('.user').last().getAttribute('data-user-id') };
}
(async () => {
    await fs.mkdir(output, { recursive: true }); temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'household-usage-browser-'));
    upstream = http.createServer(async (request, response) => {
        for await (const _ of request) { }
        const parsed = new URL(request.url, 'http://fixture');
        if (parsed.pathname.endsWith('/models')) { response.writeHead(200, { 'content-type': 'application/json' }); response.end(JSON.stringify({ data: [{ id: 'fixture-model' }] })); return; }
        if (parsed.pathname.endsWith('/count_tokens')) { response.writeHead(200, { 'content-type': 'application/json' }); response.end('{"input_tokens":123}'); return; }
        const scenario = parsed.searchParams.get('case');
        if (scenario === 'overloaded') { response.writeHead(503, { 'content-type': 'application/json', 'request-id': 'req_browser_503' }); response.end('{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}'); return; }
        if (scenario === 'sse-report' || scenario === 'crash') {
            response.writeHead(200, { 'content-type': 'text/event-stream' });
            response.write('event: message_start\ndata: {"type":"message_start","message":{"usage":{"input_tokens":11,"output_tokens":0,"cache_read_input_tokens":7,"cache_creation_input_tokens":5}}}\n\n');
            response.write('event: message_delta\ndata: {"type":"message_delta","usage":{"output_tokens":4}}\n\n');
            if (scenario === 'crash') { pending.add(response); response.on('close', () => pending.delete(response)); return; }
            response.end('event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":9,"cost_usd":0.09}}\n\nevent: message_stop\ndata: {"type":"message_stop"}\n\n'); return;
        }
        const usage = scenario === 'missing' ? undefined : scenario === 'estimated' ? { input_tokens: 30, output_tokens: 40 } : { input_tokens: 10, output_tokens: 20, cost_usd: 0.01 };
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify({ id: 'msg_fixture', type: 'message', role: 'assistant', model: 'fixture-model', content: [{ type: 'text', text: 'fixture-private-answer' }], stop_reason: 'end_turn', usage }));
    });
    await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve));
    const upstreamUrl = 'http://127.0.0.1:' + upstream.address().port;
    port = await freePort(); url = 'http://127.0.0.1:' + port; const code = await start();
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const admin = await browser.newContext({ viewport: { width: 1440, height: 1000 }, timezoneId: 'Asia/Shanghai' });
    const page = await admin.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(url + '/Setup');
    await page.getByLabel('本机引导码', { exact: true }).fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByLabel('再次输入密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '完成首次设置', exact: true }).click(); await page.waitForURL(/\/Login/); await login(page);
    a = await createUser(page, '统计甲'); b = await createUser(page, '统计乙');
    // 上游页「API 供应商」只填名称、地址、密钥，获取模型后选模型、测试、按名称切换。
    await page.goto(url + '/Upstream');
    const api = page.locator('section', { has: page.getByRole('heading', { name: 'API 供应商', exact: true }) });
    await api.getByLabel('供应商名称', { exact: true }).fill('用量测试上游');
    await api.getByLabel('上游地址', { exact: true }).fill(upstreamUrl);
    await api.getByLabel('API 密钥', { exact: true }).fill(upstreamSecret);
    await api.getByText('高级设置（一般不用改）', { exact: true }).click(); await api.getByLabel('允许连接本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: '获取模型（不花额度）', exact: true }).click();
    await page.getByLabel('模型', { exact: true }).selectOption('fixture-model');
    await page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click();
    await page.getByRole('button', { name: '切换到「用量测试上游」', exact: true }).click();
    await page.getByText('已切换到「用量测试上游」。后续请求使用它；个人令牌和中转地址不用换。', { exact: true }).waitFor();
    assert.equal((await personal(a.token)).total, 0);
    await page.goto(url + '/Usage'); await page.getByRole('link', { name: '配置模型估算费率', exact: true }).click();
    await page.getByLabel('输入单价（每百万 token）', { exact: true }).fill('1');
    await page.getByLabel('输出单价（每百万 token）', { exact: true }).fill('2');
    await page.getByLabel('缓存读取单价（每百万 token）', { exact: true }).fill('0');
    await page.getByLabel('缓存写入单价（每百万 token）', { exact: true }).fill('0');
    await page.getByRole('button', { name: '保存估算费率', exact: true }).click();
    await page.getByText('费率已保存，只影响之后发送的请求。历史金额保持不变。', { exact: true }).waitFor(); await shot(page, '01-pricing');
    const priceLine = (await page.locator('main section.card p').first().textContent()).trim();
    assert.equal(priceLine, '每百万 token 的单价：输入 1 美元，输出 2 美元，缓存读取 0 美元，缓存写入 0 美元', '费率页单价带中文币种名');
    assert.equal(await page.getByRole('link', { name: '调整「fixture-model」的费率', exact: true }).count(), 1, '费率页链接带模型名');
    await Promise.all([call(a.token, 'json-report'), call(b.token, 'missing')]); await settled(a.token, 1); await settled(b.token, 1);
    const personalContext = await browser.newContext({ viewport: { width: 1440, height: 1000 }, timezoneId: 'Asia/Shanghai' });
    const mine = await personalContext.newPage(); mine.on('pageerror', error => errors.push(error.message));
    await mine.goto(url + '/my-usage'); await mine.getByLabel('个人令牌', { exact: true }).fill(b.token);
    await mine.getByRole('button', { name: '查询我的用量', exact: true }).click(); await mine.getByText(/^查询完成。/).waitFor();
    assert.ok((await mine.locator('#results').textContent()).includes('已知输入 未知')); assert.ok(!(await mine.locator('#results').textContent()).includes('统计甲'));
    await mine.getByLabel('个人令牌', { exact: true }).fill(''); await shot(mine, '02-personal-unknown');
    await call(a.token, 'sse-report'); await call(a.token, 'count', '/v1/messages/count_tokens'); await call(a.token, 'models', '/v1/models');
    await call(b.token, 'estimated');
    const overloaded = await fetch(url + '/v1/messages?case=overloaded', { method: 'POST', headers: { Authorization: 'Bearer ' + b.token, 'Content-Type': 'application/json' }, body: JSON.stringify({ model: 'fixture-model', messages: [{ role: 'user', content: 'fixture-private-prompt' }] }) });
    assert.equal(overloaded.status, 503); assert.equal(await overloaded.text(), '{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}');
    const reportA = await settled(a.token, 4), reportB = await settled(b.token, 3);
    assert.ok(!JSON.stringify(reportB).includes('用量测试上游') && !JSON.stringify(reportB).includes('overloaded_error'), '个人用量不带上游诊断');
    assert.equal(reportA.summary.find(item => item.kind === 'inference').money[0].amount, 0.1);
    assert.equal(reportB.rows.find(row => row.money.source === 'estimated').money.amount, 0.00011);
    await page.goto(url + '/Usage'); await page.getByLabel('时间统计', { exact: true }).selectOption('hour');
    await page.getByRole('button', { name: '查询用量', exact: true }).click(); await page.getByRole('heading', { name: '按小时统计', exact: true }).waitFor();
    assert.ok((await page.textContent('main')).includes('0.1 美元')); assert.ok((await page.textContent('main')).includes('0.00011 美元')); await shot(page, '03-admin-hourly');
    assert.ok(!(await page.textContent('main')).includes(' USD'), '管理端用量页不出现裸的币种代码');
    assert.ok((await page.locator('thead').last().textContent()).includes('上游 / 原因 / 耗时'), '明细表有上游尝试列');
    const attempts = await page.locator('[data-upstream-attempts]').allTextContents();
    assert.ok(attempts.some(text => /^用量测试上游，503，上游过载，Overloaded，\d+\.\d 秒$/.test(text)), '失败行读出哪家、状态码、错误类型、说明、耗时');
    assert.ok(attempts.some(text => /^用量测试上游，200，\d+\.\d 秒$/.test(text)), '成功行读出哪家和耗时');
    await page.getByLabel('使用者', { exact: true }).selectOption(a.id);
    await page.getByLabel('开始时间（含）', { exact: true }).fill(new Date(Date.now() + 8 * 3600000 - 3600000).toISOString().slice(0, 16));
    await page.getByLabel('结束时间（不含）', { exact: true }).fill(new Date(Date.now() + 8 * 3600000 + 3600000).toISOString().slice(0, 16));
    await page.getByRole('button', { name: '查询用量', exact: true }).click();
    assert.ok((await page.textContent('main')).includes('共请求 4 次')); assert.ok(!(await page.locator('tbody').last().textContent()).includes('统计乙'));
    await page.goto(url + '/Pricing'); await page.getByLabel('输入单价（每百万 token）', { exact: true }).fill('100');
    await page.getByRole('button', { name: '保存估算费率', exact: true }).click();
    assert.equal((await personal(b.token)).rows.find(row => row.money.source === 'estimated').money.amount, 0.00011);
    await page.goto(url + '/Users'); const row = page.locator(`[data-user-id="${a.id}"]`);
    await row.getByLabel('「统计甲」的显示名', { exact: true }).fill('统计甲改名'); await row.getByRole('button', { name: '保存「统计甲」的显示名', exact: true }).click();
    await page.getByText('已保存，新的权限检查立即生效。已发往上游的请求可以结束。', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.textContent.trim()), '保存「统计甲改名」的显示名', '改名后焦点回到这一人的保存按钮');
    assert.ok((await page.title()).startsWith('已保存，新的权限检查立即生效。'), '标题带结果：' + await page.title());
    await page.locator(`[data-user-id="${a.id}"]`).getByRole('button', { name: '轮换「统计甲改名」的令牌（旧令牌立即失效）', exact: true }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'token-heading', '轮换后焦点去新令牌的标题');
    const oldToken = a.token; a.token = (await page.locator('#issued-token').textContent()).trim();
    assert.equal((await fetch(url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + oldToken } })).status, 401);
    assert.equal((await fetch(url + '/my-usage/data?user_id=' + b.id, { headers: { Authorization: 'Bearer ' + a.token } })).status, 400);
    assert.ok((await personal(a.token)).rows.every(row => String(row.userId) === a.id &&row.displayName === '统计甲改名'));
    await page.locator(`[data-user-id="${b.id}"]`).getByRole('button', { name: '停用「统计乙」（他的个人令牌立即不能用）', exact: true }).click();
    await page.getByRole('button', { name: '启用「统计乙」', exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.textContent.trim()), '启用「统计乙」', '停用后焦点回到这一人的那个按钮');
    assert.equal((await fetch(url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + b.token } })).status, 401);
    await mine.getByLabel('个人令牌', { exact: true }).fill(a.token); await mine.getByLabel('时间统计', { exact: true }).selectOption('hour');
    await mine.getByRole('button', { name: '查询我的用量', exact: true }).click(); await mine.getByText(/^查询完成。/).waitFor();
    assert.ok(!(await mine.locator('#results').textContent()).includes('统计乙'));
    await mine.getByLabel('个人令牌', { exact: true }).fill(''); await shot(mine, '04-personal-hourly');
    const crash = await call(a.token, 'crash'); const reader = crash.body.getReader(); assert.equal((await reader.read()).done, false);
    assert.equal((await personal(a.token)).summary.find(item => item.kind === 'inference').pending, 1);
    await stop(); try { await reader.cancel(); } catch { } await start();
    const restarted = await settled(a.token, 5); const interrupted = restarted.rows.find(row => row.outcome === 'interrupted');
    assert.ok(interrupted && interrupted.completeness === 'partial' && interrupted.tokens.output == null);
    assert.equal(restarted.summary.find(item => item.kind === 'inference').money[0].amount, 0.1);
    await page.goto(url + '/Usage'); await page.getByRole('heading', { name: '个人用量', exact: true }).waitFor();
    await page.setViewportSize({ width: 390, height: 844 }); await shot(page, '05-mobile-recovery');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await mine.setViewportSize({ width: 390, height: 844 }); await mine.getByLabel('个人令牌', { exact: true }).fill(a.token);
    await mine.getByRole('button', { name: '查询我的用量', exact: true }).click(); await mine.getByText(/^查询完成。/).waitFor();
    await mine.getByLabel('个人令牌', { exact: true }).fill(''); await shot(mine, '06-mobile-personal');
    assert.equal(await mine.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    // 管理端用量页：按上游筛选读出「只看哪一家」，汇总读句每个数都带单位，表头带单位，分页是带名称的导航区。
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(url + '/Usage'); await page.getByLabel('上游', { exact: true }).selectOption({ label: '用量测试上游' });
    await page.getByRole('button', { name: '查询用量', exact: true }).click();
    const onlyUpstream = (await page.locator('main p.status').first().textContent()).trim();
    assert.equal(onlyUpstream, '只看「用量测试上游」这一家上游记账的请求。要看全部，把「上游」改回「全部上游」再查询。');
    const card = (await page.locator('[data-usage-kind="inference"]').textContent()).replace(/\s+/g, ' ');
    assert.ok(card.includes('个 token') && card.includes('成功 ') && card.includes(' 次') && card.includes('美元'), card);
    const bare = card.replace(/[\d][\d,.]*(?![\d,.])( ?(次|个 token|美元))/g, '').match(/\d/g);
    assert.equal(bare, null, '汇总读句里每个数都带单位：' + card);
    assert.ok((await page.locator('thead').first().textContent()).includes('已知输入 / 输出（token）'));
    assert.equal(await page.getByRole('navigation', { name: '明细分页', exact: true }).count(), 1);
    await readingOrder(page, '07-admin-usage-by-upstream');
    // 个人用量页：筛选选项来自服务端标签表；明细超过一页时，查完念条数、焦点进结果标题；翻页时分页条不隐藏、焦点留在翻页按钮，
    // 到头的按钮隐藏、焦点挪到另一个；翻页只用已提交的条件（改了下拉框没按查询，翻页结果不变）。
    for (let index = 0; index < 21; index++) await call(a.token, 'json-report');
    await settled(a.token, 26);
    await mine.setViewportSize({ width: 1440, height: 1000 }); await mine.goto(url + '/my-usage');
    assert.deepEqual(await mine.getByLabel('接口类别', { exact: true }).locator('option').allTextContents(), ['全部接口', '推理', '估算', '模型查询'], '接口类别选项来自标签表');
    await mine.getByLabel('个人令牌', { exact: true }).fill(a.token);
    await mine.getByRole('button', { name: '查询我的用量', exact: true }).click();
    await mine.getByText(/^查询完成。共 26 条请求记录，这是第 1 页，第 1 到 25 条。$/).waitFor();
    assert.equal(await mine.evaluate(() => document.activeElement.id), 'results-heading', '查完焦点进结果标题');
    assert.equal(await mine.getByRole('button', { name: '上一页', exact: true }).count(), 0, '第一页没有「上一页」');
    assert.ok((await mine.locator('#results thead').last().textContent()).startsWith('时间'), '明细每行第一格是时间');
    assert.ok((await mine.locator('#results thead').first().textContent()).includes('缓存读 / 写（token）'), '按时间统计表有缓存读 / 写');
    await mine.getByLabel('接口类别', { exact: true }).selectOption('models');
    const next = mine.getByRole('button', { name: '下一页', exact: true }); await next.focus(); await mine.keyboard.press('Enter');
    await mine.getByText(/^查询完成。共 26 条请求记录，这是第 2 页，第 26 到 26 条。$/).waitFor();
    assert.equal(await mine.getByRole('navigation', { name: '明细分页', exact: true }).isVisible(), true, '翻页时分页条不隐藏');
    assert.equal(await mine.evaluate(() => document.activeElement.id), 'previous', '「下一页」到头隐藏后焦点挪到「上一页」');
    assert.equal(await next.count(), 0, '最后一页没有「下一页」');
    await mine.keyboard.press('Enter');
    await mine.getByText(/^查询完成。共 26 条请求记录，这是第 1 页，第 1 到 25 条。$/).waitFor();
    assert.equal(await mine.evaluate(() => document.activeElement.id), 'next', '回到第一页后焦点挪到「下一页」');
    const pageStatus = (await mine.locator('#status').textContent()).trim();
    await readingOrder(mine, '08-personal-paging');
    await mine.getByLabel('个人令牌', { exact: true }).fill(''); await shot(mine, '08-personal-paging');
    steps.push('管理端用量页按上游筛选读出：' + onlyUpstream + ' 推理汇总读句每个数都带单位（次、个 token、美元）；表头带「（token）」「（次）」；分页是名为「明细分页」的导航区。');
    steps.push('个人用量页：接口类别选项取自服务端标签表；26 条记录分两页，查完读出「' + pageStatus + '」并把焦点放进结果标题；翻到最后一页「下一页」隐藏、焦点挪到「上一页」，翻回第一页焦点挪到「下一页」；翻页前改了下拉框但没按查询，翻页结果仍是原条件。');
    assert.deepEqual(errors, []);
    steps.push('全新网页完成首次设置、两位使用者、上游测试与激活；管理员诊断未进入个人用量。');
    steps.push('上游回 503 overloaded_error 时管理员用量页末列读出「用量测试上游，503，上游过载，Overloaded，耗时」，成功行读出上游名称与耗时；个人用量数据不带上游诊断。');
    steps.push('配置费率后覆盖 JSON/SSE、上游报告金额、估算金额、未知用量、Count Tokens 与 Models 分栏；按小时和自选时间筛选。');
    steps.push('个人页面只返回本人；伪造身份被拒；轮换、改名保留历史；停用令牌不能查询。改价不改变历史估算。');
    steps.push('请求处理中强制结束独立服务进程，重启后恢复为中断/不完整；已结束记录和金额保留。390px 管理与个人页面无横向溢出。');
    await fs.writeFile(path.join(output, 'browser-results.json'), JSON.stringify({ browser: await browser.version(), mock_upstream: true, checks: steps, admin_rows: 7, personal_a: restarted, personal_b_before_disable: reportB }, null, 2));
    await fs.writeFile(path.join(output, 'browser-steps.md'), '\uFEFF# 用量浏览器验证\n\n' + steps.map(step => '- ' + step).join('\n') + '\n', 'utf8');
    await stop(); await fs.copyFile(path.join(temporary, '.state', 'relay.db'), path.join(output, 'usage-fixture.db'));
    console.log('PASS usage browser: known/unknown/money, hourly/range, rotation/disable, privacy, crash recovery and mobile');
})().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(async () => {
    if (browser) await browser.close(); await stop(); for (const response of pending) response.destroy();
    if (upstream) { upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve)); }
    if (temporary) {
        const resolved = path.resolve(temporary); assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
        assert.ok(path.basename(resolved).startsWith('household-usage-browser-')); await fs.rm(resolved, { recursive: true, force: true });
    }
});

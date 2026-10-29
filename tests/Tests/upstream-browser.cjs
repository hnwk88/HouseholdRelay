// 真实无窗口浏览器操作；仅使用本次临时实例和本机模拟上游。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const net = require('node:net');
const assert = require('node:assert/strict');
const dll = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const password = 'Fixture-browser-password-42';
const apiSecret = 'fixture-browser-api-secret';
const accountSecret = 'fixture-browser-account-secret';
let child, browser, temporary, upstream;
let rejectAccount = false;
const calls = [], steps = [];
async function port() {
    const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const value = server.address().port; await new Promise(resolve => server.close(resolve)); return value;
}
async function start(listenPort) {
    child = spawn('dotnet', [dll, 'serve', '--home', temporary, '--admin-port', String(listenPort)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    try { os.setPriority(child.pid, os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { }
    let startup = ''; child.stdout.on('data', value => startup += value.toString('utf8')); child.stderr.on('data', () => {});
    const url = `http://127.0.0.1:${listenPort}`;
    for (let attempt = 0; attempt < 100; attempt++) {
        if (child.exitCode !== null) throw new Error('临时服务启动失败');
        try { if ((await fetch(url + '/healthz')).ok && startup.includes('本机管理')) return { url, code: startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1] }; } catch { }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('临时服务启动超时');
}
async function stop() {
    if (!child || child.exitCode !== null) return;
    const stopped = new Promise(resolve => child.once('exit', resolve)); child.kill(); await stopped;
}
async function login(page, url) {
    await page.goto(url + '/Login'); await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
}
async function screenshot(page, name) {
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
}
// 读屏顺序：主内容区的无障碍树（角色、名称、层级）按页面顺序写成文本，供验收逐行核对。
async function readingOrder(page, name) {
    await fs.writeFile(path.join(output, name + '.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
}
async function candidate(page, url, upstreamUrl, mode, secret, name) {
    await page.goto(url + '/Upstream');
    const account = mode === 'oauth';
    const form = page.locator('section', { has: page.getByRole('heading', { name: account ? '官方账户' : 'API 供应商', exact: true }) });
    await form.getByLabel(account ? '账户名称' : '供应商名称', { exact: true }).fill(name);
    await form.getByLabel('上游地址', { exact: true }).fill(upstreamUrl + '/gateway/v1?tenant=browser');
    await form.getByLabel(account ? '账户访问令牌' : 'API 密钥', { exact: true }).fill(secret);
    await form.getByText('高级设置（一般不用改）', { exact: true }).click();
    if (account) await form.getByLabel('兼容中转的账户模式也需要 OAuth beta（官方地址总会附加）', { exact: true }).check();
    await form.getByLabel('允许连接本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: account ? '用账户令牌获取模型（不花额度）' : '获取模型（不花额度）', exact: true }).click();
    await page.getByText(`已保存「${name}」。已获取 1 个模型，认证用 ${account ? 'Bearer' : 'x-api-key'}。请在下面选择模型，再按测试。`, { exact: true }).waitFor();
    assert.equal(await page.getByLabel(account ? '账户访问令牌' : 'API 密钥', { exact: true }).inputValue(), '');
    assert.equal((await page.content()).includes(secret), false);
    if (!account) await readingOrder(page, '06-upstream-after-discovery');
    await page.getByLabel('模型', { exact: true }).selectOption('fixture-model');
    await page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click();
    await page.getByText(`认证用 ${account ? 'Bearer' : 'x-api-key'}，按标准 Messages 方式通过，模型 fixture-model。`, { exact: false }).first().waitFor();
    const switchButton = page.getByRole('button', { name: `切换到「${name}」`, exact: true });
    await switchButton.waitFor();
    assert.equal((await page.content()).includes(secret), false);
    await screenshot(page, account ? '08-account-tested' : '07-api-tested');
    if (!account) await readingOrder(page, '07-upstream-after-test');
    await switchButton.click();
    await page.getByText(`已切换到「${name}」。后续请求使用它；个人令牌和中转地址不用换。`, { exact: true }).waitFor();
}
async function relay(url, token, stream = false) {
    return fetch(url + '/v1/messages?beta=true', { method: 'POST', headers: { Authorization: 'Bearer ' + token, 'content-type': 'application/json', 'anthropic-beta': 'future-beta' },
        body: JSON.stringify({ model: 'fixture-model', stream, messages: [{ role: 'user', content: 'fixture-browser-call' }] }) });
}
(async () => {
    await fs.mkdir(output, { recursive: true }); temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'household-upstream-browser-'));
    upstream = http.createServer(async (request, response) => {
        let raw = ''; for await (const piece of request) raw += piece;
        const account = request.headers.authorization === 'Bearer ' + accountSecret;
        calls.push({ path: request.url, mode: account ? 'account' : 'api', beta: request.headers['anthropic-beta'] || '' });
        if (rejectAccount && account) { response.writeHead(401, { 'content-type': 'application/json' }); response.end(JSON.stringify({ type: 'error', error: { type: 'authentication_error', message: accountSecret } })); return; }
        if (request.url.includes('/models')) { response.writeHead(200, { 'content-type': 'application/json' }); response.end(JSON.stringify({ data: [{ id: 'fixture-model' }] })); return; }
        const body = JSON.parse(raw);
        if (body.stream) {
            response.writeHead(200, { 'content-type': 'text/event-stream' });
            response.write('event: ping\ndata: {"type":"ping"}\n\n');
            response.end('event: message_stop\ndata: {"type":"message_stop"}\n\n'); return;
        }
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify({ id: 'msg_fixture', type: 'message', role: 'assistant', model: 'fixture-model', content: [{ type: 'text', text: 'OK' }], stop_reason: 'end_turn' }));
    });
    await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve));
    const upstreamUrl = `http://127.0.0.1:${upstream.address().port}`, listenPort = await port();
    let { url, code } = await start(listenPort);
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    let context = await browser.newContext({ viewport: { width: 1280, height: 900 } }), page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(url + '/Setup'); await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click(); await page.waitForURL(/\/Login/); await login(page, url);
    // 全局代理：选自动检测并读出本机实际取到的代理（读数随机器而定，只记录不断言），再改回不使用代理，后续只连本机模拟上游。
    await page.goto(url + '/Settings'); await page.getByLabel('自动检测系统代理（推荐）', { exact: true }).check();
    await page.getByRole('button', { name: '保存设置', exact: true }).click(); await page.getByText(/设置已保存/).waitFor();
    const proxyStatus = (await page.locator('#proxy-status').textContent()).trim(); await readingOrder(page, '04-settings-proxy');
    await fs.writeFile(path.join(output, 'proxy-status.txt'), proxyStatus, 'utf8');
    await page.getByLabel('不使用代理', { exact: true }).check(); await page.getByRole('button', { name: '保存设置', exact: true }).click();
    await page.getByText('当前：不使用代理，直连上游。', { exact: true }).waitFor();
    steps.push('项目设置选「自动检测系统代理」后读出：' + proxyStatus + ' 随后改回不使用代理。');
    await page.goto(url + '/Users'); await page.locator('#new-name').fill('上游切换测试'); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    const token = await page.locator('#issued-token').textContent();
    await page.goto(url + '/Upstream'); await readingOrder(page, '05-upstream-new-instance');
    await candidate(page, url, upstreamUrl, 'api-key', apiSecret, '浏览器 API');
    assert.equal((await relay(url, token)).status, 200);
    assert.equal((await relay(url, token, true)).status, 200);
    await candidate(page, url, upstreamUrl, 'oauth', accountSecret, '浏览器账户');
    assert.equal((await relay(url, token)).status, 200);
    rejectAccount = true; const denied = await relay(url, token); assert.equal(denied.status, 401); assert.equal((await denied.text()).includes(accountSecret), false);
    assert.equal((await relay(url, token)).status, 503);
    await page.goto(url + '/Upstream'); await page.getByText('凭据失效，需重新接入', { exact: true }).waitFor();
    await screenshot(page, '09-account-paused'); rejectAccount = false;
    await candidate(page, url, upstreamUrl, 'oauth', accountSecret, '浏览器账户（新令牌）'); assert.equal((await relay(url, token)).status, 200);
    await page.goto(url + '/Connection'); assert.equal(await page.getByText('fixture-model', { exact: false }).count() > 0, true);
    assert.equal((await page.content()).includes(accountSecret), false); assert.equal((await page.content()).includes(token), false);
    await screenshot(page, '10-connection');
    await page.goto(url + '/Https'); await page.getByLabel('可信 HTTPS 根地址').fill('https://localhost');
    await page.getByLabel('PFX 证书文件（含私钥及中间证书链）').setInputFiles({ name: 'invalid.pfx', mimeType: 'application/x-pkcs12', buffer: Buffer.from('invalid fixture certificate') });
    await page.getByRole('button', { name: '验证并保存证书' }).click(); await page.getByText('无法读取 PFX，请检查文件和证书密码。', { exact: true }).waitFor();
    await screenshot(page, '11-certificate-rejected');
    await page.goto(url + '/Https');
    await page.getByLabel('私人 HTTPS 根地址', { exact: true }).fill('https://localhost:8787');
    await page.getByLabel('私人 HTTPS 本机监听端口', { exact: true }).fill('8787');
    await page.getByLabel('私人 HTTPS 根地址', { exact: true }).focus(); await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'private-port', '键盘应进入有标签的端口输入');
    await page.locator('form[action*="handler=Private"]').evaluate(form => form.addEventListener('submit', () => {
        const status = form.querySelector('[data-submit-status]');
        sessionStorage.setItem('fixture-loading-status', status?.textContent === '正在处理，请稍候…' && status.getAttribute('role') === 'status' ? 'seen' : 'missing');
    }));
    await page.getByRole('button', { name: '创建私有 CA 与服务器证书', exact: true }).click();
    await page.getByText(/私有 CA 及服务器证书已创建/).waitFor();
    assert.equal(await page.evaluate(() => sessionStorage.getItem('fixture-loading-status')), 'seen', '提交期间应在旧页面显示等待状态');
    await page.evaluate(() => sessionStorage.removeItem('fixture-loading-status')); await screenshot(page, '14-private-certificate-created');
    await page.goto(url + '/Connection');
    const publicPackage = await page.request.get(url + '/Connection?handler=TrustPackage'); assert.equal(publicPackage.status(), 200);
    await fs.writeFile(path.join(output, 'client-trust.zip'), await publicPackage.body());
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: url });
    await page.getByRole('button', { name: '复制信任安装命令', exact: true }).click();
    await page.getByRole('status').filter({ hasText: '已复制' }).waitFor();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), await page.locator('#trust-command').textContent());
    await page.getByRole('button', { name: '复制根地址', exact: true }).focus(); await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), 'https://localhost:8787');
    assert.ok(!(await page.content()).includes(accountSecret) && !(await page.content()).includes(token));
    await screenshot(page, '15-private-connection');
    await context.close(); await stop(); ({ url, code } = await start(listenPort)); assert.equal(code, undefined);
    context = await browser.newContext({ viewport: { width: 390, height: 844 } }); page = await context.newPage();
    await login(page, url); assert.equal((await relay(url, token)).status, 200);
    await page.goto(url + '/Upstream'); assert.equal(await page.locator('body').innerText().then(text => text.includes('已通过最小调用检测')), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false); await screenshot(page, '12-upstream-mobile');
    await page.goto(url + '/Connection'); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await screenshot(page, '13-connection-mobile'); assert.deepEqual(errors, []);
    assert.ok(calls.some(call => call.mode === 'account' && call.beta.includes('oauth-')));
    assert.ok(calls.filter(call => call.mode === 'api').every(call => !call.beta.includes('oauth-')));
    assert.ok(calls.every(call => call.path.startsWith('/gateway/v1/') && call.path.includes('tenant=browser')));
    steps.push('真实网页只填名称、地址、密钥接入 API 与账户令牌：获取模型、下拉选模型、按写明费用的按钮测试、按名称切换；既有个人令牌保持可用。读屏顺序见 *.aria.yml。');
    steps.push('实际 JSON/SSE 请求与路径前缀/查询参数通过；账户 401 后暂停，网页重新保存和激活后恢复。');
    steps.push('连接说明无上游或个人令牌回填；无效 PFX 在网页被拒绝；重启后活动配置、检测结果及调用仍有效。');
    steps.push('390px 上游和连接说明页面无水平溢出。所有模型请求仅发往本机模拟上游。');
    steps.push('网页创建私有 CA、公共包下载、表单等待提示、键盘跳转/提交和复制反馈通过；本脚本未安装系统根信任。');
    steps.push('未提供公开可信证书、域名及真实账户/API 凭据，因此公网和真实上游尚未实测。');
    await fs.writeFile(path.join(output, 'upstream-browser-steps.md'), '\uFEFF# 上游网页验证\n\n浏览器：' + await browser.version() + '\n\n' + steps.map(step => '- ' + step).join('\n') + '\n', 'utf8');
    console.log('PASS upstream browser journey: API/account setup, test, activate, JSON/SSE, credential failure, recovery, restart, certificates, connection, mobile');
})().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(async () => {
    if (browser) await browser.close(); await stop(); if (upstream) await new Promise(resolve => upstream.close(resolve));
    if (temporary) {
        const resolved = path.resolve(temporary); assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
        assert.ok(path.basename(resolved).startsWith('household-upstream-browser-')); await fs.rm(resolved, { recursive: true, force: true });
    }
});

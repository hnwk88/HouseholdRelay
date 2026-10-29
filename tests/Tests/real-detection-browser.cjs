// 显式真实联调：只填名称、地址、密钥接入两家真实上游，核对自动探测结果；凭据只从父进程管道接收，不写入日志、截图或证据。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { nativeSession, redact } = require('./real-validation-support.cjs');
const dll = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const password = crypto.randomBytes(24).toString('hex');
const expected = { aitoll: '认证用 Bearer，按 Claude Code 方式通过，模型 claude-opus-5-5。', sftoken: '认证用 x-api-key，按标准 Messages 方式通过，模型 claude-opus-5-5。' };
const children = new Set();
const evidence = { started_at: new Date().toISOString(), providers: [], switches: [] };
let server, browser, instance, material, token;
function launch(file, args, options = {}) {
    const child = spawn(file, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], ...options });
    try { os.setPriority(child.pid, os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { }
    children.add(child); child.once('exit', () => children.delete(child)); return child;
}
async function stop(child) {
    if (!child || child.exitCode !== null || child.signalCode !== null) return;
    const done = new Promise(resolve => child.once('exit', resolve)); child.kill(); await done;
}
async function freePort() {
    const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
    const port = socket.address().port; await new Promise(resolve => socket.close(resolve)); return port;
}
async function start(port) {
    server = launch('dotnet', [dll, 'serve', '--home', instance, '--admin-port', String(port)]);
    let startup = ''; server.stdout.on('data', chunk => startup += chunk.toString()); server.stderr.resume();
    const url = `http://127.0.0.1:${port}`;
    for (let i = 0; i < 150; i++) {
        assert.ok(server.exitCode === null, '临时服务启动失败');
        try { if ((await fetch(url + '/healthz')).ok && startup.includes('本机管理')) return { url, code: startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1] }; } catch { }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('临时服务未就绪');
}
async function readingOrder(page, name) {
    await fs.writeFile(path.join(output, name + '.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
}
function clean(text) { return redact(text, [password, token, ...material.providers.map(provider => provider.credential)]); }
async function status(page) {
    const notice = page.getByRole('status');
    return ((await notice.count()) > 0 ? await notice.first().textContent() : await page.getByRole('alert').first().textContent()).trim();
}
async function noSecrets(page) {
    const html = await page.content();
    assert.ok(material.providers.every(provider => !html.includes(provider.credential)), '页面不得出现上游密钥');
}
async function connect(page, url, provider) {
    const item = { name: provider.name, base_url: provider.baseUrl }; evidence.providers.push(item);
    await page.goto(url + '/Upstream');
    const form = page.locator('section', { has: page.getByRole('heading', { name: 'API 供应商', exact: true }) });
    await form.getByLabel('供应商名称', { exact: true }).fill(provider.name);
    await form.getByLabel('上游地址', { exact: true }).fill(provider.baseUrl);
    await form.getByLabel('API 密钥', { exact: true }).fill(provider.credential);
    await page.getByRole('button', { name: '获取模型（不花额度）', exact: true }).click();
    await page.getByText(`已保存「${provider.name}」。`, { exact: false }).first().waitFor({ timeout: 60000 });
    item.discovery = clean(await status(page)); await noSecrets(page);
    item.models = await page.getByLabel('模型', { exact: true }).locator('option').allTextContents();
    assert.ok(item.models.includes('claude-opus-5-5'), '模型目录必须含 claude-opus-5-5：' + item.discovery);
    await page.getByLabel('模型', { exact: true }).selectOption('claude-opus-5-5');
    await Promise.all([page.waitForURL(/handler=Test/, { timeout: 120000 }), page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click()]);
    item.test = clean(await status(page)); await noSecrets(page);
    item.detected_matches_known = item.test.startsWith(expected[provider.name]);
    await page.screenshot({ path: path.join(output, provider.name + '-tested.png'), fullPage: true });
    await readingOrder(page, provider.name + '-after-test');
    assert.ok(item.detected_matches_known, `${provider.name} 自动探测结果与已知组合不符：${item.test}`);
}
async function switchTo(page, url, provider) {
    await page.goto(url + '/Upstream');
    await page.getByRole('button', { name: `切换到「${provider.name}」`, exact: true }).click();
    await page.getByText(`已切换到「${provider.name}」。后续请求使用它；个人令牌和中转地址不用换。`, { exact: true }).waitFor();
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-detect-client-'));
    try {
        evidence.stage = provider.name + ':native';
        const session = await nativeSession({ launch, material, token, url, model: 'claude-opus-5-5', tool: false, directory, caPath: null, evidence, output });
        evidence.switches.push({ name: provider.name, native: session });
    } finally { await fs.rm(directory, { recursive: true, force: true }); }
}
(async () => {
    let input = ''; for await (const chunk of process.stdin) input += chunk.toString(); material = JSON.parse(input); input = '';
    await fs.mkdir(output, { recursive: true }); instance = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-detect-browser-'));
    const port = await freePort(); const { url, code } = await start(port);
    browser = await chromium.launch({ channel: 'msedge', headless: true }); evidence.browser = await browser.version();
    const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    await page.goto(url + '/Setup'); await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click(); await page.waitForURL(/\/Login/);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
    evidence.stage = 'proxy-settings';
    await page.goto(url + '/Settings'); await page.getByLabel('自动检测系统代理（推荐）', { exact: true }).check();
    await page.getByRole('button', { name: '保存设置', exact: true }).click(); await page.getByText(/设置已保存/).waitFor();
    await page.goto(url + '/Settings'); evidence.proxy_status = (await page.locator('#proxy-status').textContent()).trim();
    await readingOrder(page, 'settings-proxy');
    assert.equal(evidence.proxy_status, '当前：自动检测到 http://127.0.0.1:7897。', '自动检测必须读出本机系统代理');
    await page.goto(url + '/Users'); await page.locator('#new-name').fill('探测验证'); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    token = (await page.locator('#issued-token').textContent()).trim();
    for (const provider of material.providers) { evidence.stage = provider.name + ':connect'; await connect(page, url, provider); }
    for (const provider of material.providers) await switchTo(page, url, provider);
    const usage = await (await fetch(url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + token } })).json();
    evidence.relay_requests = usage.rows.map(row => ({ request_id: row.requestId, upstream_version: row.upstreamVersion, kind: row.kind, model: row.model, outcome: row.outcome, tokens: row.tokens }));
    assert.ok(evidence.relay_requests.some(row => row.outcome === 'succeeded'), '经中转的 Claude Code 调用必须记入用量');
    evidence.stage = 'completed';
    console.log('真实探测完成：' + evidence.providers.map(item => item.name + ' → ' + item.test).join('；'));
})().catch(error => {
    evidence.error = material ? clean(String(error.message)) : String(error.message).slice(0, 2000);
    console.error(evidence.error); process.exitCode = 1;
}).finally(async () => {
    for (const child of [...children]) await stop(child);
    if (browser) await browser.close();
    await fs.writeFile(path.join(output, 'real-detection-results.json'), JSON.stringify(evidence, null, 2));
    if (instance) {
        const resolved = path.resolve(instance);
        if (path.dirname(resolved).toLowerCase() === path.resolve(os.tmpdir()).toLowerCase() && path.basename(resolved).startsWith('HouseholdRelay-detect-browser-'))
            await fs.rm(resolved, { recursive: true, force: true });
    }
});

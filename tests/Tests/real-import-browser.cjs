// 显式真实联调：把一份新做的官方登录文件经网页「导入登录文件」接进临时实例，测试、切换后用真实 Claude Code 客户端经中转调一次；令牌不写入日志、截图或证据。
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
const children = new Set();
const evidence = { started_at: new Date().toISOString() };
let server, browser, instance, material, token, secrets = [];
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
function clean(text) { return redact(text, [password, token, ...secrets]); }
async function said(page) {
    const notice = page.getByRole('status');
    return ((await notice.count()) > 0 ? await notice.first().textContent() : await page.getByRole('alert').first().textContent()).trim();
}
async function noSecrets(page) { const html = await page.content(); assert.ok(secrets.every(secret => !html.includes(secret)), '页面不得出现登录文件里的令牌'); }
(async () => {
    let input = ''; for await (const chunk of process.stdin) input += chunk.toString(); material = JSON.parse(input); input = '';
    const content = await fs.readFile(material.file);
    const oauth = JSON.parse(content.toString('utf8')).claudeAiOauth;
    secrets = [oauth.accessToken, oauth.refreshToken].filter(Boolean);
    // nativeSession 按 providers[].credential 脱敏客户端输出，这里把登录文件里的两个令牌交给它。
    material.providers = secrets.map(credential => ({ credential }));
    await fs.mkdir(output, { recursive: true }); instance = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-import-browser-'));
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
    await page.goto(url + '/Users'); await page.locator('#new-name').fill('导入验证'); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    token = (await page.locator('#issued-token').textContent()).trim();
    evidence.stage = 'import';
    await page.goto(url + '/Upstream');
    const form = page.locator('section', { has: page.getByRole('heading', { name: '官方账户', exact: true }) });
    await form.getByLabel('登录文件（.credentials.json）', { exact: true }).setInputFiles({ name: '.credentials.json', mimeType: 'application/json', buffer: content });
    await page.getByRole('button', { name: '导入登录文件', exact: true }).click();
    await page.getByRole('status').filter({ hasText: '登录文件已导入' }).or(page.getByRole('alert')).first().waitFor({ timeout: 120000 });
    evidence.import = clean(await said(page)); await noSecrets(page);
    assert.ok(evidence.import.startsWith('登录文件已导入，已保存为「'), evidence.import);
    const name = evidence.import.match(/已保存为「(.+?)」/)[1];
    evidence.models = await page.getByLabel('模型', { exact: true }).locator('option').allTextContents();
    // 测试与客户端调用都用目录里最便宜的一档：有 haiku 用 haiku，否则 sonnet，否则第一个。
    const model = evidence.models.find(item => item.includes('haiku')) ?? evidence.models.find(item => item.includes('sonnet')) ?? evidence.models[0];
    assert.ok(model, '导入后必须读到模型目录：' + evidence.import); evidence.model = model;
    evidence.stage = 'test';
    await page.getByLabel('模型', { exact: true }).selectOption(model);
    await Promise.all([page.waitForURL(/handler=Test/, { timeout: 120000 }), page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click()]);
    evidence.test = clean(await said(page)); await noSecrets(page);
    await page.screenshot({ path: path.join(output, 'import-tested.png'), fullPage: true });
    await fs.writeFile(path.join(output, 'import-after-test.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
    evidence.stage = 'switch';
    await page.getByRole('button', { name: `切换到「${name}」`, exact: true }).click();
    await page.getByText(`已切换到「${name}」。后续请求使用它；个人令牌和中转地址不用换。`, { exact: true }).waitFor();
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-import-client-'));
    try {
        evidence.stage = 'native';
        evidence.native = await nativeSession({ launch, material, token, url, model, tool: false, directory, caPath: null, evidence, output });
    } finally { await fs.rm(directory, { recursive: true, force: true }); }
    const usage = await (await fetch(url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + token } })).json();
    evidence.relay_requests = usage.rows.map(row => ({ request_id: row.requestId, upstream_version: row.upstreamVersion, kind: row.kind, model: row.model, outcome: row.outcome, tokens: row.tokens }));
    assert.ok(evidence.relay_requests.some(row => row.outcome === 'succeeded'), '经中转的 Claude Code 调用必须记入用量');
    evidence.stage = 'completed';
    console.log('真实导入完成：' + evidence.import + ' 测试：' + evidence.test + ` 经中转请求 ${evidence.relay_requests.length} 次。`);
})().catch(error => {
    evidence.error = clean(String(error.message)).slice(0, 2000);
    console.error(evidence.error); process.exitCode = 1;
}).finally(async () => {
    for (const child of [...children]) await stop(child);
    if (browser) await browser.close();
    await fs.writeFile(path.join(output, 'real-import-results.json'), JSON.stringify(evidence, null, 2));
    if (instance) {
        const resolved = path.resolve(instance);
        if (path.dirname(resolved).toLowerCase() === path.resolve(os.tmpdir()).toLowerCase() && path.basename(resolved).startsWith('HouseholdRelay-import-browser-'))
            await fs.rm(resolved, { recursive: true, force: true });
    }
});

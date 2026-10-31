// 显式真实联调：两套临时实例 A、B，在 A 只填名称、地址、密钥接入一家真实上游，经网页导出加密文件；在 B 经网页导入，测试、切换后用真实 Claude Code 经 B 调一次。
// 凭据只从父进程管道接收，不写入日志、截图或证据；两套实例都在系统临时目录，不碰正式数据目录，端口随机。
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
const password = crypto.randomBytes(24).toString('hex'), exportPassword = crypto.randomBytes(16).toString('hex');
const model = 'claude-opus-5-5';
const children = new Set(), instances = [];
const evidence = { started_at: new Date().toISOString() };
let browser, material, token;
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
// 起一套临时实例：数据目录在系统临时目录、管理页端口随机；返回地址与首次设置引导码。
async function start(label) {
    const home = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-transfer-' + label + '-')); instances.push(home);
    const port = await freePort();
    const server = launch('dotnet', [dll, 'serve', '--home', home, '--admin-port', String(port)]);
    let startup = ''; server.stdout.on('data', chunk => startup += chunk.toString()); server.stderr.resume();
    const url = `http://127.0.0.1:${port}`;
    for (let i = 0; i < 150; i++) {
        assert.ok(server.exitCode === null, '临时服务启动失败：' + label);
        try { if ((await fetch(url + '/healthz')).ok && startup.includes('本机管理')) return { url, code: startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1] }; } catch { }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('临时服务未就绪：' + label);
}
// 首次设置、登录、项目设置改为自动检测系统代理（与正式实例同一出口）。
async function prepare(page, { url, code }) {
    await page.goto(url + '/Setup'); await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click(); await page.waitForURL(/\/Login/);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
    await page.goto(url + '/Settings'); await page.getByLabel('自动检测系统代理（推荐）', { exact: true }).check();
    await page.getByRole('button', { name: '保存设置', exact: true }).click(); await page.getByText(/设置已保存/).waitFor();
    await page.goto(url + '/Settings'); return (await page.locator('#proxy-status').textContent()).trim();
}
async function readingOrder(page, name) {
    await fs.writeFile(path.join(output, name + '.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
}
function clean(text) { return redact(text, [password, exportPassword, token, ...material.providers.map(provider => provider.credential)]); }
// 页面读出的那句话：成功读含 text 的状态；失败读校验摘要里的错误（摘要区平时也在，只认带错误的那种）。
async function said(page, text) {
    const found = page.getByRole('status').filter({ hasText: text }).or(page.locator('.validation-summary-errors'));
    await found.first().waitFor({ timeout: 120000 }); return clean((await found.first().textContent()).trim());
}
async function noSecrets(page) {
    const html = await page.content();
    assert.ok(material.providers.every(provider => !html.includes(provider.credential)), '页面不得出现上游密钥');
}
(async () => {
    let input = ''; for await (const chunk of process.stdin) input += chunk.toString(); material = JSON.parse(input); input = '';
    const provider = material.providers[0]; evidence.provider = { name: provider.name, base_url: provider.baseUrl };
    await fs.mkdir(output, { recursive: true });
    browser = await chromium.launch({ channel: 'msedge', headless: true }); evidence.browser = await browser.version();

    // A：只填名称、地址、密钥接入（获取模型不花额度），然后经网页导出。
    const a = await start('a'); const pageA = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    evidence.a_proxy = await prepare(pageA, a);
    await pageA.goto(a.url + '/Upstream');
    const api = pageA.locator('section', { has: pageA.getByRole('heading', { name: 'API 供应商', exact: true }) });
    await api.getByLabel('供应商名称', { exact: true }).fill(provider.name);
    await api.getByLabel('上游地址', { exact: true }).fill(provider.baseUrl);
    await api.getByLabel('API 密钥', { exact: true }).fill(provider.credential);
    await pageA.getByRole('button', { name: '获取模型（不花额度）', exact: true }).click();
    evidence.a_discovery = await said(pageA, `已保存「${provider.name}」。`); await noSecrets(pageA);
    const transferA = pageA.locator('section', { has: pageA.getByRole('heading', { name: '搬到另一套中转：导出与导入', exact: true }) });
    await transferA.getByLabel('导出密码（12 到 128 个字符，导入时要用，不会保存）', { exact: true }).fill(exportPassword);
    await transferA.getByLabel('再输入一次导出密码', { exact: true }).fill(exportPassword);
    const downloading = pageA.waitForEvent('download');
    await transferA.getByRole('button', { name: '导出加密文件', exact: true }).click();
    const download = await downloading; evidence.a_export = await said(pageA, '已导出'); await noSecrets(pageA);
    const exportPath = path.join(os.tmpdir(), 'HouseholdRelay-transfer-' + crypto.randomBytes(6).toString('hex') + '.hrupstreams');
    await download.saveAs(exportPath);
    const file = await fs.readFile(exportPath);
    evidence.export_file = { name: download.suggestedFilename(), bytes: file.length, header: file.subarray(0, 10).toString('ascii'), contains_key: file.includes(Buffer.from(provider.credential)) };
    assert.equal(evidence.export_file.contains_key, false, '导出文件里不得出现明文密钥');
    await readingOrder(pageA, 'a-exported');

    // B：一套全新的实例，经网页导入同一个文件；按原名出现、尚未测试。随后选模型测试（花少量额度）、切换。
    const b = await start('b'); const pageB = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    evidence.b_proxy = await prepare(pageB, b);
    await pageB.goto(b.url + '/Upstream');
    const transferB = pageB.locator('section', { has: pageB.getByRole('heading', { name: '搬到另一套中转：导出与导入', exact: true }) });
    await transferB.getByLabel('导出文件', { exact: true }).setInputFiles(exportPath);
    await transferB.getByLabel('导出时设的密码', { exact: true }).fill(exportPassword);
    await transferB.getByRole('button', { name: '导入上游', exact: true }).click();
    evidence.b_import = await said(pageB, '已导入'); await noSecrets(pageB);
    assert.ok(evidence.b_import.startsWith(`已导入 1 家上游：「${provider.name}」，都排在已有上游后面，尚未测试。`), evidence.b_import);
    await fs.rm(exportPath, { force: true });
    await readingOrder(pageB, 'b-imported');
    await pageB.getByRole('button', { name: `重新获取「${provider.name}」的模型（不花额度）`, exact: true }).click();
    evidence.b_discovery = await said(pageB, `已重新获取「${provider.name}」的模型。`);
    await pageB.getByLabel('模型', { exact: true }).selectOption(model);
    await Promise.all([pageB.waitForURL(/handler=Test/, { timeout: 120000 }), pageB.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click()]);
    evidence.b_test = await said(pageB, '认证用'); await noSecrets(pageB);
    await pageB.getByRole('button', { name: `切换到「${provider.name}」`, exact: true }).click();
    evidence.b_switch = await said(pageB, `已切换到「${provider.name}」。`);
    await pageB.screenshot({ path: path.join(output, 'b-switched.png'), fullPage: true }); await readingOrder(pageB, 'b-switched');

    // 真实 Claude Code 经 B 调一次，再读 B 的个人用量。
    await pageB.goto(b.url + '/Users'); await pageB.locator('#new-name').fill('导入验证'); await pageB.getByRole('button', { name: '创建并生成令牌' }).click();
    token = (await pageB.locator('#issued-token').textContent()).trim();
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-transfer-client-'));
    try { evidence.stage = 'native'; evidence.native = await nativeSession({ launch, material, token, url: b.url, model, tool: false, directory, caPath: null, evidence, output }); }
    finally { await fs.rm(directory, { recursive: true, force: true }); }
    const usage = await (await fetch(b.url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + token } })).json();
    evidence.b_requests = usage.rows.map(row => ({ request_id: row.requestId, kind: row.kind, model: row.model, outcome: row.outcome, tokens: row.tokens }));
    assert.ok(evidence.b_requests.some(row => row.outcome === 'succeeded'), '经 B 的 Claude Code 调用必须记入用量');
    evidence.stage = 'completed';
    console.log('真实导出导入完成：' + evidence.b_import + ' / ' + evidence.b_test);
})().catch(error => {
    evidence.error = material ? clean(String(error.message)) : String(error.message).slice(0, 2000);
    console.error(evidence.error); process.exitCode = 1;
}).finally(async () => {
    for (const child of [...children]) await stop(child);
    if (browser) await browser.close();
    await fs.writeFile(path.join(output, 'real-transfer-results.json'), JSON.stringify(evidence, null, 2));
    for (const home of instances) {
        const resolved = path.resolve(home);
        if (path.dirname(resolved).toLowerCase() === path.resolve(os.tmpdir()).toLowerCase() && path.basename(resolved).startsWith('HouseholdRelay-transfer-'))
            await fs.rm(resolved, { recursive: true, force: true });
    }
});

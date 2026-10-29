// 运行：node browser-journey.cjs 主程序DLL 证据目录；NODE_PATH 指向已安装的 playwright。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const assert = require('node:assert/strict');

const dll = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const password = 'Browser-fixture-password-42';
let child, browser, home;
const steps = [];

async function freePort() {
    const server = net.createServer();
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const port = server.address().port;
    await new Promise(resolve => server.close(resolve));
    return port;
}
async function start(port) {
    child = spawn('dotnet', [dll, 'serve', '--home', home, '--admin-port', String(port)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    try { os.setPriority(child.pid, os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { /* 部分运行身份不能改优先级。 */ }
    let startup = '';
    child.stdout.on('data', value => { startup += value.toString('utf8'); });
    child.stderr.on('data', () => {});
    const url = `http://127.0.0.1:${port}`;
    for (let attempt = 0; attempt < 100; attempt++) {
        if (child.exitCode !== null) throw new Error('服务在启动完成前退出。');
        try {
            const result = await fetch(url + '/healthz');
            if (result.ok && startup.includes('本机管理')) return { url, code: startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1] };
        } catch { /* 等待本次服务绑定端口，不访问外部模型。 */ }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('本机管理服务启动超时。');
}
async function stop() {
    if (!child || child.exitCode !== null) return;
    const stopped = new Promise(resolve => child.once('exit', resolve));
    child.kill(); await stopped;
}
async function cli(...args) {
    const command = spawn('dotnet', [dll, 'clients', ...args, '--home', home], { windowsHide: true, stdio: 'ignore' });
    const code = await new Promise(resolve => command.once('exit', resolve));
    assert.equal(code, 0, 'CLI 修改失败');
}
async function shot(page, name) {
    // 截图前隐藏一次性令牌；不保存密码、Cookie、引导码或原始响应。
    await page.locator('[data-sensitive]').evaluateAll(nodes => nodes.forEach(node => node.textContent = '已隐藏：个人令牌仅在生成时显示一次'));
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
}
async function login(page, url) {
    await page.goto(url + '/Login');
    await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录', exact: true }).click();
    await page.waitForURL(url + '/');
}

(async () => {
    await fs.mkdir(output, { recursive: true });
    home = await fs.mkdtemp(path.join(os.tmpdir(), 'household-relay-browser-'));
    const port = await freePort();
    let { url, code } = await start(port); assert.ok(code);
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    let context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    let page = await context.newPage();
    const browserErrors = [];
    page.on('pageerror', error => browserErrors.push(error.message));
    await page.goto(url); await page.waitForURL(url + '/Setup');
    await shot(page, '01-first-setup');
    await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click();
    await page.waitForLoadState('load');
    assert.match(page.url(), /\/Login\?setup=True/i, '首次设置未跳转：' + await page.locator('body').innerText());
    steps.push('干净目录启动，无 Claude Code 初始化依赖；浏览器完成一次性管理员设置。');
    await login(page, url); await shot(page, '02-overview');
    assert.equal(await page.getByText('尚未配置 HTTPS', { exact: true }).count(), 1);
    await page.getByRole('link', { name: '使用者与令牌', exact: true }).click();
    await page.locator('#new-name').fill('甲');
    await page.getByRole('button', { name: '创建并生成令牌' }).click();
    const firstToken = await page.locator('#issued-token').textContent();
    const aId = await page.locator('.user').first().getAttribute('data-user-id');
    assert.match(firstToken, /^hr_/);
    await shot(page, '03-issued-token-redacted');
    await page.goto(url + '/Users'); assert.equal(await page.locator('#issued-token').count(), 0);
    await page.locator('#new-name').fill('乙'); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    const secondToken = await page.locator('#issued-token').textContent();
    const bId = await page.locator('.user').last().getAttribute('data-user-id');
    await page.goto(url + '/Users');
    const a = () => page.locator(`[data-user-id="${aId}"]`);
    const b = () => page.locator(`[data-user-id="${bId}"]`);
    await b().getByRole('button', { name: '停用', exact: true }).click();
    assert.ok((await b().textContent()).includes('已停用'));
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + secondToken } })).status(), 401);
    await a().getByRole('button', { name: '轮换令牌' }).click();
    const rotatedToken = await page.locator('#issued-token').textContent(); assert.notEqual(rotatedToken, firstToken);
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + firstToken } })).status(), 401);
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + rotatedToken } })).status(), 200);
    await page.goto(url + '/Users');
    await b().getByLabel('显示名', { exact: true }).fill('乙（网页与 CLI 并发修改）');
    await Promise.all([b().getByRole('button', { name: '保存显示名' }).click(), cli('enable', '--id', bId)]);
    await page.goto(url + '/Users');
    assert.ok((await b().textContent()).includes('已启用'));
    assert.equal(await b().getByLabel('显示名', { exact: true }).inputValue(), '乙（网页与 CLI 并发修改）');
    await b().getByRole('button', { name: '停用', exact: true }).click();
    await shot(page, '04-users');
    steps.push('网页创建甲乙，令牌只显示一次；停用乙和轮换甲后，实际 /status 请求立即拒绝旧令牌。');
    steps.push('网页改显示名与独立 CLI 进程启用同一人并发执行，两项修改均保留。');
    await page.getByRole('link', { name: '项目设置', exact: true }).click();
    await page.getByLabel('对外地址（可稍后填写）').fill('https://relay.example.com:5433');
    await page.getByLabel('中转监听端口').fill('9797');
    await page.getByRole('button', { name: '保存设置' }).click();
    assert.ok((await page.locator('[role="status"]').textContent()).includes('设置已保存'));
    await shot(page, '05-settings');
    await context.close(); await stop();
    ({ url, code } = await start(port)); assert.equal(code, undefined);
    context = await browser.newContext({ viewport: { width: 1280, height: 900 } }); page = await context.newPage();
    await login(page, url); await page.goto(url + '/Users');
    assert.equal(await page.locator('.user').count(), 2);
    assert.ok((await b().textContent()).includes('已停用'));
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + rotatedToken } })).status(), 200);
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + firstToken } })).status(), 401);
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + secondToken } })).status(), 401);
    await page.goto(url + '/Settings'); assert.equal(await page.getByLabel('中转监听端口').inputValue(), '9797');
    assert.equal((await context.request.get(url + '/Setup')).status(), 404);
    await page.setViewportSize({ width: 390, height: 844 }); await page.goto(url + '/Users'); await shot(page, '06-mobile-after-restart');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, '窄屏出现水平溢出');
    await page.getByRole('button', { name: '退出登录', exact: true }).click(); await page.waitForURL(url + '/Login');
    await page.goto(url + '/Users'); await page.waitForURL(/\/Login/);
    assert.deepEqual(browserErrors, []);
    steps.push('停止并重新启动后，管理密码、使用者 ID、启停状态、令牌哈希和项目设置仍然有效；首次设置已锁定。');
    steps.push('验证 390px 窄屏布局无水平溢出；退出后受保护页面重新要求登录。');
    await fs.writeFile(path.join(output, 'browser-steps.md'), '\uFEFF# 浏览器验证记录\n\n' + steps.map(step => '- ' + step).join('\n') + '\n\n浏览器：' + await browser.version() + '\n仅使用临时目录和虚构凭据；无真实上游调用。\n', 'utf8');
    console.log('PASS browser journey: setup, login, users, revocation, CLI concurrency, settings, restart, mobile, logout');
})().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(async () => {
    if (browser) await browser.close(); await stop();
    if (home) {
        const resolved = path.resolve(home);
        assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
        assert.ok(path.basename(resolved).startsWith('household-relay-browser-'));
        await fs.rm(resolved, { recursive: true, force: true });
    }
});

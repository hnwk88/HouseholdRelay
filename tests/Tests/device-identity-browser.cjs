// 运行：node device-identity-browser.cjs 主程序DLL 证据目录；只使用本机临时站点、虚构凭据，不启动 Claude。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const { DatabaseSync } = require('node:sqlite');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const assert = require('node:assert/strict');
const dll = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const password = 'Fixture-device-maintenance-42';
let child, browser, temporary, url, listenPort;
const checks = [], evidence = { mock_only: true, native_cli_started: false, official_requests: 0 };

async function freePort() {
    const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
    const port = socket.address().port; await new Promise(resolve => socket.close(resolve)); return port;
}
async function start() {
    child = spawn('dotnet', [dll, 'serve', '--home', temporary, '--admin-port', String(listenPort)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let startup = ''; child.stdout.on('data', bytes => startup += bytes.toString('utf8')); child.stderr.on('data', () => {});
    for (let attempt = 0; attempt < 120; attempt++) {
        if (child.exitCode !== null) throw new Error('临时设备维护站点提前退出。');
        try { if ((await fetch(url + '/healthz')).ok && startup.includes('本机管理')) return startup.match(/首次设置引导码（仅供本机输入）：(hr_\S+)/)?.[1]; }
        catch { /* 仅等待本机端口，不访问任何上游。 */ }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('临时站点未按时启动。');
}
async function stop() {
    if (!child || child.exitCode !== null) return;
    const exited = new Promise(resolve => child.once('exit', resolve)); child.kill(); await exited;
}
function snapshot() {
    const database = new DatabaseSync(path.join(temporary, '.state', 'relay.db'), { readOnly: true });
    try {
        const state = database.prepare('SELECT version,operation_id,pending FROM site_identity WHERE id=1').get();
        const config = JSON.parse(database.prepare('SELECT json FROM settings WHERE id=1').get().json);
        return { ...state, device: config.deviceId };
    } finally { database.close(); }
}
async function form(page, handler = 'Refresh') {
    const current = page.locator(`form[action$="handler=${handler}"]`);
    return current.locator('input[type=hidden]').evaluateAll(inputs => Object.fromEntries(inputs.map(input => [input.name, input.value])));
}
async function login(page) {
    await page.goto(url + '/Login');
    if (page.url() === url + '/') return;
    await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
}
async function post(context, handler, values) {
    return context.request.post(url + '/DeviceIdentity?handler=' + handler, { form: values, headers: { Origin: url } });
}
async function record(page, name) {
    const state = snapshot(), html = await page.content();
    assert.equal(html.includes(state.device), false, '页面不能展示设备原值');
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true });
    await fs.writeFile(path.join(output, name + '.aria.yml'), await page.locator('main').ariaSnapshot(), 'utf8');
}

(async () => {
    await fs.mkdir(output, { recursive: true }); temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'household-device-browser-'));
    listenPort = await freePort(); url = `http://127.0.0.1:${listenPort}`; const code = await start(); assert.ok(code);
    browser = await chromium.launch({ channel: 'msedge', headless: true }); evidence.browser = await browser.version();
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage(), errors = []; page.on('pageerror', error => errors.push(error.message));
    const anonymous = await context.request.get(url + '/DeviceIdentity', { maxRedirects: 0 });
    assert.equal(anonymous.status(), 302); checks.push('未登录访问设备维护被重定向到登录。');
    await page.goto(url + '/Setup'); await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click(); await page.waitForURL(/\/Login/); await login(page);
    await page.goto(url + '/Users'); await page.locator('#new-name').fill('设备维护测试');
    await page.getByRole('button', { name: '创建并生成令牌' }).click(); const personalToken = await page.locator('#issued-token').textContent();
    await page.goto(url + '/DeviceIdentity'); const initial = snapshot();
    assert.match(await page.locator('#identity-progress[role=status]').innerText(), /已完成/);
    const original = await form(page); await page.reload(); await page.reload(); assert.deepEqual(snapshot(), initial);
    checks.push('反复 GET 仅查看状态，不改设备编号、版本或操作历史。'); await record(page, '01-ready');
    const csrfMissing = await post(context, 'Refresh', { OperationId: original.OperationId, Version: original.Version, Confirmed: 'true' });
    assert.equal(csrfMissing.status(), 400); assert.deepEqual(snapshot(), initial);
    const unconfirmed = await post(context, 'Refresh', { ...original, Confirmed: 'false' });
    assert.equal(unconfirmed.status(), 200);
    const confirmationError = await page.evaluate(html => new DOMParser().parseFromString(html, 'text/html').querySelector('[role=alert]').textContent, await unconfirmed.text());
    assert.match(confirmationError, /请先确认暂停范围和更换影响/); assert.deepEqual(snapshot(), initial);
    checks.push('没有 CSRF 的更换被拒绝；未确认影响的提交在页面报错，两者均未改号。');
    await page.reload(); const operation = await form(page);
    await page.getByLabel('我确认暂时暂停本站全部官方账户，并更换设备编号；这不能解除账户限制。').check();
    await page.getByRole('button', { name: '更换设备编号', exact: true }).click(); await page.waitForLoadState('load');
    assert.match(await page.locator('main').innerText(), /这次设备维护已完成/);
    const refreshed = snapshot(); assert.equal(refreshed.version, initial.version + 1); assert.notEqual(refreshed.device, initial.device); assert.equal(refreshed.pending, 0);
    const repeated = await post(context, 'Refresh', { ...operation, Confirmed: 'true' }); assert.equal(repeated.status(), 200); assert.deepEqual(snapshot(), refreshed);
    await page.reload(); await record(page, '02-refresh-complete'); checks.push('管理员确认后更换一次；重复提交同一操作仍为同一版本和编号。');
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + personalToken } })).status(), 200);
    checks.push('既有个人令牌在设备维护后仍可调用自己的状态入口。');
    await page.getByRole('button', { name: '开启临时取证', exact: true }).click(); await page.waitForLoadState('load');
    assert.deepEqual(snapshot(), refreshed); await record(page, '03-diagnostics');
    checks.push('开启临时取证只改变取证状态，不更换身份、不试发请求。');

    // 只在本次测试临时站点中放入坏配置，触发提交后派生配置失败；不存在任何真实账户记录。
    const account = path.join(temporary, '.state', 'official-accounts', '1234567890abcdef1234567890abcdef'); await fs.mkdir(account, { recursive: true });
    const config = path.join(account, '.claude.json'), credentials = path.join(account, '.credentials.json');
    const fixtureCredentials = Buffer.from('{"fixture":"never-read-or-refreshed"}'); await fs.writeFile(credentials, fixtureCredentials); await fs.writeFile(config, '{broken');
    await page.reload(); const failedOperation = await form(page);
    await page.getByLabel('我确认暂时暂停本站全部官方账户，并更换设备编号；这不能解除账户限制。').check();
    await page.getByRole('button', { name: '更换设备编号', exact: true }).click(); await page.waitForLoadState('load');
    await page.getByRole('button', { name: '继续原操作', exact: true }).waitFor();
    const pending = snapshot(); assert.equal(pending.version, refreshed.version + 1); assert.equal(pending.pending, 1);
    assert.equal(pending.operation_id, failedOperation.OperationId); assert.notEqual(pending.device, refreshed.device);
    assert.match(await page.locator('[role=alert]').innerText(), /未完成/); assert.match(await page.locator('#identity-progress').innerText(), /未完成/);
    await record(page, '04-pending'); assert.deepEqual(await fs.readFile(credentials), fixtureCredentials);
    const retryBeforeRepair = await post(context, 'Resume', await form(page, 'Resume')); assert.equal(retryBeforeRepair.status(), 200); assert.deepEqual(snapshot(), pending);
    await stop(); await start(); await login(page); await page.goto(url + '/DeviceIdentity');
    assert.deepEqual(snapshot(), pending); await page.getByRole('button', { name: '继续原操作', exact: true }).waitFor();
    assert.match(await page.locator('main').innerText(), /取证.*未开启|取证.*关闭/);
    checks.push('坏私有配置使已提交更换保持未完成；重试和重启均沿用同一目标，取证重启后关闭。');
    await fs.writeFile(config, '{"machineID":"fixture-machine-preserved","unknown":{"escaped":"\\u4e2d"},"userID":"old-fixture"}');
    await page.getByRole('button', { name: '继续原操作', exact: true }).click(); await page.waitForLoadState('load');
    const resumed = snapshot(); assert.equal(resumed.version, pending.version); assert.equal(resumed.device, pending.device); assert.equal(resumed.pending, 0);
    const prepared = JSON.parse(await fs.readFile(config, 'utf8')); assert.equal(prepared.userID, pending.device); assert.equal(prepared.machineID, 'fixture-machine-preserved');
    assert.deepEqual(await fs.readFile(credentials), fixtureCredentials); assert.equal((await fs.readdir(account)).some(name => name.startsWith('fake-')), false);
    checks.push('修复配置后继续原操作成功，未生成新代次，machineID 与凭据原字节保持。');
    await page.setViewportSize({ width: 390, height: 844 }); await page.reload();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false); await record(page, '05-mobile-ready');
    assert.equal(await page.locator('#identity-progress[role=status]').count(), 1); assert.equal(await page.locator('[role=alert]').count(), 1);
    assert.equal((await context.request.get(url + '/status', { headers: { Authorization: 'Bearer ' + personalToken } })).status(), 200);
    checks.push('390px 无水平溢出；状态和错误区域具备读屏角色，维护恢复后个人令牌仍有效。');
    assert.deepEqual(errors, []); evidence.versions = { initial: initial.version, refreshed: refreshed.version, pending: pending.version, resumed: resumed.version };
    evidence.checks = checks; evidence.passed = true;
    await fs.writeFile(path.join(output, 'browser-results.json'), JSON.stringify(evidence, null, 2), 'utf8');
    console.log('PASS device identity browser: access, CSRF, confirmation, idempotency, pending, restart, recovery, diagnostics, privacy, mobile');
})().catch(async error => {
    evidence.passed = false; evidence.error = error.message.split('\n')[0]; evidence.checks = checks;
    await fs.mkdir(output, { recursive: true }); await fs.writeFile(path.join(output, 'browser-results.json'), JSON.stringify(evidence, null, 2), 'utf8');
    console.error(error.name + ': ' + evidence.error + '\n' + (error.stack ?? '').split('\n').filter(line => /^\s+at /.test(line)).join('\n')); process.exitCode = 1;
}).finally(async () => {
    if (browser) await browser.close(); await stop();
    if (temporary) {
        const resolved = path.resolve(temporary); assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
        assert.ok(path.basename(resolved).startsWith('household-device-browser-'));
        await fs.rm(resolved, { recursive: true, force: true });
    }
});

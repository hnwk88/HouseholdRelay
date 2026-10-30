// 真实无窗口浏览器操作；仅使用本次临时实例和本机模拟上游。
// 运行：node upstream-browser.cjs 主程序DLL 证据目录 [测试程序Tests.exe]；不给第三个参数时取本目录 bin\Release 下构建好的 Tests.exe。
// 「登录官方账户」用假 Claude Code：把 Tests.exe 复制成同目录的 claude.exe 放到子服务 PATH 最前面，场景写在临时文件里，结束时删掉。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const { readdirSync, existsSync } = require('node:fs');
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
// 与 FakeClaude.cs 的 FirstToken、Email 一致：假登录写进账户目录的令牌和 auth status 报告的邮箱。
const loginToken = 'fixture-login-token-first', loginEmail = 'family.member@example.com';
// 「导入登录文件」上传的那份凭据里的访问令牌；模拟上游据此认出导入的上游。
const importToken = 'fixture-browser-imported-token';
const testsExe = process.argv[4] ? path.resolve(process.argv[4]) : readdirSync(path.join(__dirname, 'bin', 'Release'))
    .map(name => path.join(__dirname, 'bin', 'Release', name, 'Tests.exe')).find(existsSync);
const fakeClaude = path.join(path.dirname(testsExe ?? '.'), 'claude.exe');
let child, browser, temporary, upstream, scenarioFile;
let rejectAccount = false, regionBlocked = false;
const regionSecret = 'fixture-browser-region-secret';
const calls = [], steps = [];
async function port() {
    const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const value = server.address().port; await new Promise(resolve => server.close(resolve)); return value;
}
async function start(listenPort) {
    // Windows 的环境变量名不分大小写，沿用现有的 Path 键名，免得出现两个 PATH。
    const pathKey = Object.keys(process.env).find(key => key.toUpperCase() === 'PATH') ?? 'PATH';
    const env = { ...process.env, [pathKey]: path.dirname(fakeClaude) + path.delimiter + process.env[pathKey], HOUSEHOLD_FAKE_CLAUDE_SCENARIO_FILE: scenarioFile };
    child = spawn('dotnet', [dll, 'serve', '--home', temporary, '--admin-port', String(listenPort)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env });
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
    await form.getByLabel(account ? '贴令牌用的上游地址' : '上游地址', { exact: true }).fill(upstreamUrl + '/gateway/v1?tenant=browser');
    await form.getByLabel(account ? '账户访问令牌' : 'API 密钥', { exact: true }).fill(secret);
    await form.getByText(account ? '贴令牌的高级设置（一般不用改）' : '高级设置（一般不用改）', { exact: true }).click();
    if (account) await form.getByLabel('兼容中转的账户模式也需要 OAuth beta（官方地址总会附加）', { exact: true }).check();
    await form.getByLabel(account ? '允许贴令牌用的上游是本机或私网地址（包括 HTTP）' : '允许连接本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: account ? '用账户令牌获取模型（不花额度）' : '获取模型（不花额度）', exact: true }).click();
    await page.getByText(`已保存「${name}」。已获取 1 个模型，认证用 ${account ? 'Bearer' : 'x-api-key'}。请在下面选择模型，再按测试。`, { exact: true }).waitFor();
    assert.equal(await page.getByLabel(account ? '账户访问令牌' : 'API 密钥', { exact: true }).inputValue(), '');
    assert.equal((await page.content()).includes(secret), false);
    if (!account) await readingOrder(page, '06-upstream-after-discovery');
    await testAndSwitch(page, name, account ? 'Bearer' : 'x-api-key', account ? '08-account-tested' : '07-api-tested', secret, account ? null : '07-upstream-after-test');
}
// 第二步：下拉选模型、按写明费用的测试按钮、按名称切换；接 API、贴令牌、登录官方账户都走这一步。
async function testAndSwitch(page, name, authentication, shot, secret, order) {
    await page.getByLabel('模型', { exact: true }).selectOption('fixture-model');
    await page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click();
    await page.getByText(`认证用 ${authentication}，按标准 Messages 方式通过，模型 fixture-model。`, { exact: false }).first().waitFor();
    const switchButton = page.getByRole('button', { name: `切换到「${name}」`, exact: true });
    await switchButton.waitFor();
    assert.equal((await page.content()).includes(secret), false);
    await screenshot(page, shot);
    if (order) await readingOrder(page, order);
    await switchButton.click();
    await page.getByText(`已切换到「${name}」。后续请求使用它；个人令牌和中转地址不用换。`, { exact: true }).waitFor();
}
// 按「登录官方账户」：场景文件告诉假 claude.exe 这次是取消还是成功；返回页面读出的那句话（成功读状态，失败读错误）。
async function officialLogin(page, url, upstreamUrl, scenario, name) {
    await fs.writeFile(scenarioFile, scenario, 'utf8');
    await page.goto(url + '/Upstream');
    const form = page.locator('section', { has: page.getByRole('heading', { name: '官方账户', exact: true }) });
    await form.getByLabel('账户名称（可不填，不填就用登录邮箱）', { exact: true }).fill(name);
    await form.getByText('登录的高级设置（一般不用改）', { exact: true }).click();
    await form.getByLabel('登录用的上游地址', { exact: true }).fill(upstreamUrl + '/gateway/v1?tenant=browser');
    await form.getByLabel('允许登录用的上游是本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: '登录官方账户', exact: true }).click();
    const said = page.getByRole('status').filter({ hasText: '官方账户登录成功' }).or(page.getByRole('alert').filter({ hasText: '登录' }));
    await said.first().waitFor({ timeout: 60000 });
    return (await said.first().textContent()).trim();
}
// 按「导入登录文件」：选一份凭据文件（buffer），名称可空；返回页面读出的那句话（成功读状态，失败读错误）。
async function importLogin(page, url, upstreamUrl, content, name) {
    await page.goto(url + '/Upstream');
    const form = page.locator('section', { has: page.getByRole('heading', { name: '官方账户', exact: true }) });
    await form.getByLabel('登录文件（.credentials.json）', { exact: true }).setInputFiles({ name: '.credentials.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(content)) });
    await form.getByLabel('导入的账户名称（可不填，不填就用登录邮箱）', { exact: true }).fill(name);
    await form.getByText('导入的高级设置（一般不用改）', { exact: true }).click();
    await form.getByLabel('导入用的上游地址', { exact: true }).fill(upstreamUrl + '/gateway/v1?tenant=browser');
    await form.getByLabel('允许导入用的上游是本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: '导入登录文件', exact: true }).click();
    const said = page.getByRole('status').filter({ hasText: '登录文件已导入' }).or(page.getByRole('alert').filter({ hasText: '文件' }));
    await said.first().waitFor({ timeout: 60000 });
    return (await said.first().textContent()).trim();
}
// 同一区域内可访问名称相同的输入框：展开全部高级设置后，按 section 收集输入框、下拉框的标签文字与折叠标题，返回清点结果。
async function duplicateNames(page) {
    await page.locator('details').evaluateAll(items => items.forEach(item => { item.open = true; }));
    const sections = await page.locator('main section').evaluateAll(items => items.map(section => ({
        section: section.querySelector('h2')?.textContent.trim() ?? '',
        inputs: [...section.querySelectorAll('input:not([type=hidden]), select, textarea')]
            .map(input => (input.labels?.[0]?.textContent ?? input.getAttribute('aria-label') ?? '').trim()),
        summaries: [...section.querySelectorAll('summary')].map(summary => summary.textContent.trim())
    })));
    const repeated = list => list.filter((name, index) => list.indexOf(name) !== index);
    return {
        inputs: sections.reduce((sum, item) => sum + item.inputs.length, 0),
        duplicates: sections.flatMap(item => [...repeated(item.inputs), ...repeated(item.summaries)].map(name => item.section + '：' + name)), sections
    };
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
        const mode = account ? 'account' : request.headers.authorization === 'Bearer ' + loginToken ? 'login' : request.headers.authorization === 'Bearer ' + importToken ? 'import' : 'api';
        calls.push({ path: request.url, mode, beta: request.headers['anthropic-beta'] || '' });
        // 模拟按地区屏蔽当前网络出口：照 sftoken 现场回 503 和一张说明网页。
        if (regionBlocked) { response.writeHead(503, { 'content-type': 'text/html; charset=utf-8' }); response.end('<title>该网页在当前地区不可用</title>'); return; }
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
    assert.ok(testsExe, '找不到 Tests.exe：请先构建 tests/Tests，或把它的路径作为第三个参数');
    scenarioFile = path.join(temporary, 'fake-claude-scenario.txt'); await fs.writeFile(scenarioFile, 'cancel', 'utf8');
    await fs.copyFile(testsExe, fakeClaude);
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
    // 上游回 503（地区屏蔽）：读句指向代理出口而不是地址；上游恢复后对已保存的上游按「重新获取模型」，不重贴密钥、不新增上游；再删掉它，已保存数量减 1。
    const savedCount = async () => Number((await page.getByRole('heading', { name: /^已保存的上游（\d+ 个）$/ }).textContent()).match(/\d+/)[0]);
    regionBlocked = true; await page.goto(url + '/Upstream');
    const api = page.locator('section', { has: page.getByRole('heading', { name: 'API 供应商', exact: true }) });
    await api.getByLabel('供应商名称', { exact: true }).fill('顺丰中转');
    await api.getByLabel('上游地址', { exact: true }).fill(upstreamUrl + '/gateway/v1?tenant=browser');
    await api.getByLabel('API 密钥', { exact: true }).fill(regionSecret);
    await api.getByText('高级设置（一般不用改）', { exact: true }).click();
    await api.getByLabel('允许连接本机或私网地址（包括 HTTP）', { exact: true }).check();
    await page.getByRole('button', { name: '获取模型（不花额度）', exact: true }).click();
    const blocked = page.getByRole('status').filter({ hasText: '拒绝了当前网络出口' }); await blocked.waitFor();
    const blockedText = (await blocked.textContent()).trim();
    assert.equal(blockedText, '已保存「顺丰中转」。获取模型失败，上游返回 HTTP 503。上游暂时不可用，或者拒绝了当前网络出口（例如按地区屏蔽）。当前代理设置：不使用代理，直连上游。请检查代理出口后重试。');
    assert.equal(blockedText.includes('检查地址'), false); assert.equal((await page.content()).includes(regionSecret), false);
    await screenshot(page, '19-region-blocked'); const before = await savedCount();
    regionBlocked = false;
    await page.getByRole('button', { name: '重新获取「顺丰中转」的模型（不花额度）', exact: true }).click();
    const again = page.getByRole('status').filter({ hasText: '已重新获取「顺丰中转」的模型。' }); await again.waitFor();
    const againText = (await again.textContent()).trim();
    assert.ok(againText.startsWith('已重新获取「顺丰中转」的模型。已获取 1 个模型'), againText);
    assert.equal(await savedCount(), before, '重新获取模型不得新增上游');
    assert.equal(await page.getByRole('button', { name: '删除「浏览器账户（新令牌）」', exact: true }).count(), 0, '当前使用中的上游不能出删除按钮');
    await page.getByLabel('确认删除「顺丰中转」（删除后不能恢复）', { exact: true }).check();
    await page.getByRole('button', { name: '删除「顺丰中转」', exact: true }).click();
    await page.getByRole('status').filter({ hasText: '已删除「顺丰中转」。' }).waitFor();
    assert.equal(await savedCount(), before - 1); assert.equal(await page.getByRole('heading', { name: '顺丰中转', exact: true }).count(), 0);
    await readingOrder(page, '20-upstream-after-delete'); await screenshot(page, '20-upstream-after-delete');
    steps.push('上游回 503（模拟地区屏蔽）时读出：' + blockedText + ' 恢复后按「重新获取模型」读出：' + againText + ` 已保存数量仍为 ${before}；勾选确认后删除「顺丰中转」，已保存数量变为 ${before - 1}；当前使用中的上游没有删除按钮。`);
    // 登录官方账户（假 Claude Code）：先取消，读出原因、不新增上游、不留账户目录；再成功，名称留空用登录邮箱，测试、切换后转发用登录令牌。
    const accounts = path.join(temporary, '.state', 'official-accounts');
    const cancelled = await officialLogin(page, url, upstreamUrl, 'cancel', '取消的登录');
    assert.equal(cancelled, '登录没有完成（Claude Code 登录以退出码 1 结束），可能是在浏览器里取消了。需要时再按「登录官方账户」重试。');
    assert.equal((await page.locator('main').innerText()).includes('取消的登录'), false);
    assert.deepEqual(existsSync(accounts) ? readdirSync(accounts) : [], []);
    await screenshot(page, '16-official-login-cancelled');
    const loggedIn = await officialLogin(page, url, upstreamUrl, 'success', '');
    assert.ok(loggedIn.startsWith(`官方账户登录成功，已保存为「${loginEmail}」。已获取 1 个模型`), loggedIn);
    assert.equal((await page.content()).includes(loginToken), false);
    const audit = await duplicateNames(page);
    await fs.writeFile(path.join(output, 'duplicate-names.json'), JSON.stringify(audit, null, 2), 'utf8');
    assert.deepEqual(audit.duplicates, [], '同一区域里不能有可访问名称相同的输入框或折叠标题');
    const accountBlock = page.locator('section', { has: page.getByRole('heading', { name: '官方账户', exact: true }) });
    await fs.writeFile(path.join(output, '17-official-account-block.aria.yml'), await accountBlock.ariaSnapshot(), 'utf8');
    await testAndSwitch(page, loginEmail, 'Bearer', '18-official-login-tested', loginToken);
    assert.equal((await relay(url, token)).status, 200);
    assert.ok(calls.some(call => call.mode === 'login' && call.path.includes('/messages')));
    assert.equal(readdirSync(accounts).length, 1);
    const windows = (await fs.readFile(path.join(accounts, readdirSync(accounts)[0], 'fake-windows.log'), 'utf8')).trim().split('\n');
    await fs.writeFile(path.join(output, 'fake-claude-windows.log'), windows.join('\n') + '\n', 'utf8');
    assert.ok(windows.some(line => line.startsWith('auth login\t')) && windows.every(line => line.endsWith('\tFalse')), '登录子进程不得在屏幕上出窗口：' + windows.join(' | '));
    steps.push('登录官方账户（假 Claude Code）取消时读出：' + cancelled + ' 没有新增上游、没有留下账户目录。');
    steps.push('登录官方账户（假 Claude Code）成功、名称留空时读出：' + loggedIn + ' 随后测试、按名称切换，转发用登录得到的令牌；假 CLI 每次调用都没有可见窗口。');
    steps.push(`上游页按区域清点输入框 ${audit.inputs} 个，同一区域内可访问名称相同的 ${audit.duplicates.length} 处（明细见 duplicate-names.json）。`);
    // 导入登录文件（假 Claude Code 取邮箱）：先导入没有刷新令牌的文件，读出原因、不新增上游、不多账户目录；再导入合格文件，名称留空得邮箱，测试、切换后转发用导入的令牌。
    const oauth = refreshToken => ({ claudeAiOauth: { accessToken: importToken, refreshToken, expiresAt: Date.now() + 3600000, scopes: ['user:inference'] } });
    const savedBefore = await savedCount(), accountsBefore = readdirSync(accounts).length;
    const importRefused = await importLogin(page, url, upstreamUrl, oauth(undefined), '不合格的导入');
    assert.equal(importRefused, '选的文件里只有访问令牌、没有刷新令牌（refreshToken），导入后到期不能自动续期。请改用下面的「贴账户访问令牌」。');
    assert.equal(await savedCount(), savedBefore); assert.equal(readdirSync(accounts).length, accountsBefore);
    assert.equal((await page.content()).includes(importToken), false);
    await screenshot(page, '21-import-refused');
    const imported = await importLogin(page, url, upstreamUrl, oauth('fixture-browser-imported-refresh'), '');
    assert.ok(imported.startsWith(`登录文件已导入，已保存为「${loginEmail}」。已获取 1 个模型`), imported);
    assert.equal(await savedCount(), savedBefore + 1); assert.equal(readdirSync(accounts).length, accountsBefore + 1);
    assert.equal((await page.content()).includes(importToken), false);
    await testAndSwitch(page, loginEmail, 'Bearer', '22-import-tested', importToken, '22-upstream-after-import');
    assert.equal((await relay(url, token)).status, 200);
    assert.ok(calls.some(call => call.mode === 'import' && call.path.includes('/messages')));
    steps.push('导入登录文件（没有刷新令牌）时读出：' + importRefused + ' 没有新增上游、没有多出账户目录。');
    steps.push('导入登录文件（合格、名称留空）时读出：' + imported + ' 随后测试、按名称切换，转发用导入文件里的令牌。');
    await page.goto(url + '/Connection'); assert.equal(await page.getByText('fixture-model', { exact: false }).count() > 0, true);
    assert.equal((await page.content()).includes(accountSecret), false); assert.equal((await page.content()).includes(token), false);
    await screenshot(page, '10-connection');
    await page.goto(url + '/Https'); await page.getByLabel('可信 HTTPS 根地址').fill('https://localhost');
    await page.getByLabel('PFX 证书文件（含私钥及中间证书链）').setInputFiles({ name: 'invalid.pfx', mimeType: 'application/x-pkcs12', buffer: Buffer.from('invalid fixture certificate') });
    await page.getByRole('button', { name: '验证并保存证书' }).click(); await page.getByText('无法读取 PFX，请检查文件和证书密码。', { exact: true }).waitFor();
    await screenshot(page, '11-certificate-rejected');
    await page.goto(url + '/Https');
    // 私人 HTTPS 用空闲端口：本机正式部署常驻 8787，写死会让后面重启的临时实例监听失败。
    const privatePort = await port();
    await page.getByLabel('私人 HTTPS 根地址', { exact: true }).fill(`https://localhost:${privatePort}`);
    await page.getByLabel('私人 HTTPS 本机监听端口', { exact: true }).fill(String(privatePort));
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
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), `https://localhost:${privatePort}`);
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
    console.log('PASS upstream browser journey: API/account setup, test, activate, JSON/SSE, credential failure, recovery, official login cancel/success, login file import refused/success, restart, certificates, connection, mobile');
})().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(async () => {
    if (browser) await browser.close(); await stop(); if (upstream) await new Promise(resolve => upstream.close(resolve));
    // 只删本脚本复制出来的那个 claude.exe，不碰同目录的 Tests.exe。
    if (testsExe && path.basename(fakeClaude) === 'claude.exe') await fs.rm(fakeClaude, { force: true });
    if (temporary) {
        const resolved = path.resolve(temporary); assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
        assert.ok(path.basename(resolved).startsWith('household-upstream-browser-')); await fs.rm(resolved, { recursive: true, force: true });
    }
});

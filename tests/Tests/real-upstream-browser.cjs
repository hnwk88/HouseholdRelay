// 显式真实联调：凭据只从父进程匿名管道接收，不写入日志、截图或证据 JSON。
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { secureFetch, trustFixture, nativeSession, redact } = require('./real-validation-support.cjs');
const dll = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const clientOnly = process.argv[4] === '--client-only';
// 两家真实上游都用这个模型验证（与 real-detection-browser.cjs 相同）。
const model = 'claude-opus-5-5';
let server, browser, interceptor, instance, material, token, authorityPem, caPath, relayUrl, fingerprint, secondUser;
const evidence = { started_at: new Date().toISOString(), providers: [], server_stopped: false };
const password = crypto.randomBytes(24).toString('hex');
const children = new Set();
const relayFetch = (url, options) => secureFetch(url, options, authorityPem);
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
async function login(page, url) {
    await page.goto(url + '/Login'); await page.getByLabel('管理密码', { exact: true }).fill(password);
    await page.getByRole('button', { name: '登录', exact: true }).click(); await page.waitForURL(url + '/');
}
// 新上游表单：只填名称、地址、密钥 → 获取模型 → 选模型 → 按写明费用的按钮测试 → 按名称切换；认证方式和检测方式由程序探测，返回测试读句。
async function candidate(page, url, provider) {
    await page.goto(url + '/Upstream');
    const form = page.locator('section', { has: page.getByRole('heading', { name: 'API 供应商', exact: true }) });
    await form.getByLabel('供应商名称', { exact: true }).fill(provider.name);
    await form.getByLabel('上游地址', { exact: true }).fill(provider.baseUrl);
    await form.getByLabel('API 密钥', { exact: true }).fill(provider.credential);
    await page.getByRole('button', { name: '获取模型（不花额度）', exact: true }).click();
    await page.getByText(`已保存「${provider.name}」。`, { exact: false }).first().waitFor({ timeout: 60000 });
    assert.ok(!(await page.content()).includes(provider.credential), '获取模型后的页面不得回显凭据');
    await page.getByLabel('模型', { exact: true }).selectOption(model);
    await page.getByRole('button', { name: '测试（会消耗少量上游额度）', exact: true }).click();
    const activate = page.getByRole('button', { name: `切换到「${provider.name}」`, exact: true });
    await activate.waitFor({ timeout: 120000 });
    const tested = (await page.getByRole('status').first().textContent()).trim();
    assert.ok(!(await page.content()).includes(provider.credential), '测试后的页面不得回显凭据');
    await page.screenshot({ path: path.join(output, provider.name + '-tested.png'), fullPage: true });
    await switchTo(page, url, provider); return tested;
}
// 按已保存上游列表里的「切换到」按钮切换；切回先前测过的上游不用重新接入。
async function switchTo(page, url, provider) {
    await page.goto(url + '/Upstream');
    await page.getByRole('button', { name: `切换到「${provider.name}」`, exact: true }).click();
    await page.getByText(`已切换到「${provider.name}」。后续请求使用它；个人令牌和中转地址不用换。`, { exact: true }).waitFor();
}
async function native(url, model, tool = false) {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-rework-client-'));
    return nativeSession({ launch, material, token, url, model, tool, directory, caPath, evidence, output });
}
async function call(url, body, headers, stream, personalToken = token) {
    const outgoing = { 'content-type': 'application/json', Authorization: 'Bearer ' + personalToken };
    for (const name of ['user-agent', 'anthropic-beta', 'anthropic-version', 'x-app']) if (headers[name]) outgoing[name] = headers[name];
    const started = Date.now();
    const response = await relayFetch(url + '/v1/messages?beta=true', { method: 'POST', headers: outgoing, body: JSON.stringify({ ...body, stream }), signal: AbortSignal.timeout(60000) });
    let content = '', chunks = 0, firstChunkMs = null; const decoder = new TextDecoder();
    for await (const chunk of response.body) { firstChunkMs ??= Date.now() - started; chunks++; content += decoder.decode(chunk, { stream: true }); }
    content += decoder.decode();
    assert.ok(material.providers.every(provider => !content.includes(provider.credential)), '响应不得泄露上游凭据');
    assert.ok(response.status === 200, '真实转发请求未成功');
    if (stream) {
        const events = [...content.matchAll(/^event: (.+)$/gm)].map(match => match[1]);
        assert.ok(events.includes('message_stop') && !events.includes('error'), 'SSE 必须完整结束');
        const usage = {};
        for (const match of content.matchAll(/^data: (.+)$/gm)) {
            let data; try { data = JSON.parse(match[1]); } catch { continue; }
            if (data.type === 'message_start') Object.assign(usage, data.message?.usage || {});
            if (data.type === 'message_delta') Object.assign(usage, data.usage || {});
        }
        return { status: response.status, request_id: response.headers.get('x-relay-request-id'), events, usage, chunks, first_chunk_ms: firstChunkMs, elapsed_ms: Date.now() - started, tls: url.startsWith('https:') };
    }
    const message = JSON.parse(content); assert.ok(message.type === 'message' && message.role === 'assistant', 'JSON 必须为真实 Messages 回复');
    return { status: response.status, request_id: response.headers.get('x-relay-request-id'), model: message.model, usage: message.usage, elapsed_ms: Date.now() - started };
}
async function personalUsage(url, personalToken = token) {
    const response = await relayFetch(url + '/my-usage/data', { headers: { Authorization: 'Bearer ' + personalToken } });
    assert.equal(response.status, 200, '个人用量查询必须成功'); return response.json();
}
async function verifyUsage(url, call, personalToken = token) {
    for (let attempt = 0; attempt < 50; attempt++) {
        const report = await personalUsage(url, personalToken); const row = report.rows.find(item => item.requestId === call.request_id);
        if (row && row.outcome !== 'started') {
            assert.equal(row.outcome, 'succeeded'); assert.equal(row.completeness, 'complete');
            for (const [source, target] of [['input_tokens', 'input'], ['output_tokens', 'output'], ['cache_read_input_tokens', 'cacheRead'], ['cache_creation_input_tokens', 'cacheWrite']])
                assert.equal(row.tokens[target], call.usage?.[source] ?? null, '持久化用量必须与实际响应一致');
            assert.ok(row.money.source !== 'estimated', '未配置费率时不能猜测费用');
            return { request_id: row.requestId, user_id: row.userId, upstream_version: row.upstreamVersion, tokens: row.tokens, money: row.money, completeness: row.completeness };
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('个人用量未及时完成持久化');
}
async function createUser(page, url, name) {
    await page.goto(url + '/Users'); await page.locator('#new-name').fill(name); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    const issued = { token: (await page.locator('#issued-token').textContent()).trim(), id: await page.locator('.user').last().getAttribute('data-user-id') };
    await page.goto(url + '/Users'); assert.equal(await page.locator('#issued-token').count(), 0, '令牌只能展示一次'); return issued;
}
async function configureTls(page, url, port) {
    evidence.stage = 'private-ca-web-creation';
    const tlsPort = await freePort(); relayUrl = `https://127.0.0.1:${tlsPort}`;
    await page.goto(url + '/Https'); await page.getByLabel('私人 HTTPS 根地址', { exact: true }).fill(relayUrl);
    await page.getByLabel('私人 HTTPS 本机监听端口', { exact: true }).fill(String(tlsPort));
    await page.getByRole('button', { name: '创建私有 CA 与服务器证书', exact: true }).click();
    await page.getByText(/私有 CA 及服务器证书已创建/).waitFor();
    await page.screenshot({ path: path.join(output, 'https-created.png'), fullPage: true });
    await page.goto(url + '/Connection'); fingerprint = await page.locator('#ca-fingerprint').textContent();
    const download = await page.request.get(url + '/Connection?handler=TrustPackage'); assert.equal(download.status(), 200);
    await fs.writeFile(path.join(output, 'client-trust.zip'), await download.body());
    await trustFixture(launch, 'Prepare', output, fingerprint);
    authorityPem = await fs.readFile(path.join(output, 'trust', 'relay-ca.pem'), 'utf8');
    assert.ok(!authorityPem.includes('PRIVATE KEY'));
    await stop(server); await start(port);
    await assert.rejects(() => secureFetch(relayUrl + '/healthz'), '未安装信任必须失败');
    await assert.rejects(() => secureFetch(relayUrl + '/healthz', {}, authorityPem, 'wrong-name.invalid'), '错误名称必须失败');
    if (clientOnly) {
        caPath = path.join(output, 'trust', 'relay-ca.pem');
        evidence.private_tls = { root_sha256: fingerprint, url: relayUrl, untrusted_rejected: true, wrong_name_rejected: true,
            fingerprint_mismatch_rejected: true, normal_certificate_validation: true, client_ca_file: true,
            windows_trust_installation: 'not_executed', browser_trust: 'not_executed', reason: 'Windows root confirmation requires the operator; this run independently checks client TLS.' };
        await page.context().clearCookies(); await login(page, url); return page;
    }
    evidence.stage = 'client-trust-installation';
    await trustFixture(launch, 'Install', output, fingerprint);
    await trustFixture(launch, 'Install', output, fingerprint);
    const installed = JSON.parse(await trustFixture(launch, 'Inspect', output, fingerprint, relayUrl)); caPath = installed.ca_path;
    evidence.private_tls = { root_sha256: fingerprint, url: relayUrl, untrusted_rejected: true, wrong_name_rejected: true,
        fingerprint_mismatch_rejected: true, windows_tls_status: installed.windows_tls_status, installer_repeatable: true, normal_certificate_validation: true };
    await browser.close(); browser = await chromium.launch({ channel: 'msedge', headless: true });
    const fresh = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    await fresh.goto(relayUrl + '/healthz'); assert.equal((await fresh.locator('body').textContent()).includes('ok'), true);
    evidence.private_tls.browser_trust = true; await login(fresh, url); return fresh;
}
(async () => {
    let input = ''; for await (const chunk of process.stdin) input += chunk.toString(); material = JSON.parse(input); input = '';
    await fs.mkdir(output, { recursive: true }); instance = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-rework-browser-'));
    evidence.temporary_instance = instance;
    const port = await freePort(); let { url, code } = await start(port);
    browser = await chromium.launch({ channel: 'msedge', headless: true }); evidence.browser = await browser.version();
    let page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    await page.goto(url + '/Setup'); await page.getByLabel('本机引导码').fill(code);
    await page.getByLabel('管理密码', { exact: true }).fill(password); await page.getByLabel('再次输入密码').fill(password);
    await page.getByRole('button', { name: '完成首次设置' }).click(); await page.waitForURL(/\/Login/); await login(page, url);
    page = await configureTls(page, url, port);
    const firstUser = await createUser(page, url, '真实甲'); token = firstUser.token; secondUser = await createUser(page, url, '真实乙');
    evidence.users = [firstUser.id, secondUser.id];
    let captured;
    // 仅从本机模拟响应收集原生请求形状；真实会话直接连接产品 HTTPS，不经过截获代理。
    interceptor = http.createServer(async (request, response) => {
        try {
            const chunks = []; for await (const chunk of request) chunks.push(chunk); const body = Buffer.concat(chunks);
            if (request.url.startsWith('/v1/messages') && !request.url.includes('count_tokens')) {
                const message = JSON.parse(body); captured = { body: message, headers: request.headers };
                response.writeHead(200, { 'content-type': 'text/event-stream' });
                const event = (type, value) => response.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...value })}\n\n`);
                event('message_start', { message: { id: 'msg_shape_fixture', type: 'message', role: 'assistant', model: message.model, content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 0 } } });
                event('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
                event('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'OK' } }); event('content_block_stop', { index: 0 });
                event('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 1 } }); event('message_stop', {}); response.end(); return;
            }
            response.writeHead(200, { 'content-type': 'application/json' }); response.end('{"input_tokens":1,"data":[]}');
        } catch { response.destroy(); }
    });
    await new Promise(resolve => interceptor.listen(0, '127.0.0.1', resolve)); const clientUrl = `http://127.0.0.1:${interceptor.address().port}`;
    // 代理照全局设置：真实上游按地区屏蔽时只有经代理出口才能连上，与 real-detection-browser.cjs 一样选自动检测并记下读数。
    evidence.stage = 'proxy-settings'; await page.goto(url + '/Settings');
    await page.getByLabel('自动检测系统代理（推荐）', { exact: true }).check();
    await page.getByRole('button', { name: '保存设置', exact: true }).click(); await page.getByText(/设置已保存/).waitFor();
    evidence.proxy_status = (await page.locator('#proxy-status').textContent()).trim();
    for (const provider of material.providers) {
        const item = { name: provider.name, base_url: provider.baseUrl, model };
        evidence.stage = provider.name + ':candidate'; evidence.providers.push(item); const beforeProbe = await personalUsage(relayUrl);
        item.test = redact(await candidate(page, url, provider), [provider.credential]); item.web_test_and_activation = true;
        assert.equal((await personalUsage(relayUrl)).total, beforeProbe.total, '管理员检测不能进入个人用量');
        evidence.stage = provider.name + ':offline-request-shape'; await native(clientUrl, model); const simple = captured;
        evidence.stage = provider.name + ':native-https'; item.native = await native(relayUrl, model);
        item.client_version = simple.headers['user-agent'];
        evidence.stage = provider.name + ':json-sse-two-users';
        [item.json, item.sse] = await Promise.all([call(relayUrl, simple.body, simple.headers, false), call(relayUrl, simple.body, simple.headers, true, secondUser.token)]);
        item.json.persisted_usage = await verifyUsage(relayUrl, item.json); item.sse.persisted_usage = await verifyUsage(relayUrl, item.sse, secondUser.token);
        assert.equal(item.json.persisted_usage.user_id, firstUser.id); assert.equal(item.sse.persisted_usage.user_id, secondUser.id);
        assert.ok(!(await personalUsage(relayUrl)).rows.some(row => row.userId === secondUser.id));
        evidence.stage = provider.name + ':tool-https'; item.tool_session = await native(relayUrl, model, true); item.tool_roundtrip = true;
        await page.goto(url + '/Connection'); assert.ok(!(await page.content()).includes(provider.credential) && !(await page.content()).includes(token), '连接说明不能包含秘密');
        if (item.test.includes('按 Claude Code 方式通过')) await page.getByText(/当前上游已用 Claude Code 实际调用验证/).waitFor();
        await page.screenshot({ path: path.join(output, provider.name + '-connection.png'), fullPage: true });
        await stop(server); ({ url } = await start(port)); await page.context().clearCookies(); await login(page, url);
        evidence.stage = provider.name + ':restart-https';
        item.after_restart = await call(relayUrl, simple.body, simple.headers, false);
        item.after_restart.persisted_usage = await verifyUsage(relayUrl, item.after_restart);
        const retained = await personalUsage(relayUrl); assert.ok(retained.rows.some(row => row.requestId === item.json.request_id), '重启必须保留旧调用');
        item.personal_summary = retained.summary;
        await page.goto(url + '/Usage'); await page.getByRole('heading', { name: '个人用量', exact: true }).waitFor();
        await page.screenshot({ path: path.join(output, provider.name + '-usage.png'), fullPage: true });
        await fs.writeFile(path.join(output, 'real-upstream-results.json'), JSON.stringify(evidence, null, 2));
        console.log('真实上游完成：' + provider.name + '，网页/JSON/SSE/工具/重启通过');
    }
    evidence.stage = 'switch-back-https'; await switchTo(page, url, material.providers[0]); evidence.switch_back = await native(relayUrl, model);
    evidence.stage = 'rotate-disable-private-usage'; await page.goto(url + '/Users');
    await page.locator(`[data-user-id="${firstUser.id}"]`).getByRole('button', { name: '轮换令牌', exact: true }).click();
    const oldToken = token; token = (await page.locator('#issued-token').textContent()).trim();
    assert.equal((await relayFetch(relayUrl + '/my-usage/data', { headers: { Authorization: 'Bearer ' + oldToken } })).status, 401);
    assert.ok((await personalUsage(relayUrl)).rows.some(row => row.requestId === evidence.providers[0].json.request_id));
    await page.goto(url + '/Users'); await page.locator(`[data-user-id="${secondUser.id}"]`).getByRole('button', { name: '停用', exact: true }).click();
    for (const route of ['/my-usage/data', '/v1/models']) assert.equal((await relayFetch(relayUrl + route, { headers: { Authorization: 'Bearer ' + secondUser.token } })).status, 401);
    evidence.rotation_and_disable = true;
    await page.setViewportSize({ width: 390, height: 844 }); await page.goto(url + '/Upstream');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), '窄屏不得水平溢出');
    await page.screenshot({ path: path.join(output, 'narrow-upstream.png'), fullPage: true }); evidence.narrow_screen = true;
    await page.goto(url + '/Connection'); assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
    await page.screenshot({ path: path.join(output, 'narrow-connection.png'), fullPage: true });
    const personal = await browser.newPage({ viewport: { width: 390, height: 844 } }); await personal.goto((clientOnly ? url : relayUrl) + '/my-usage');
    await personal.getByLabel('个人令牌', { exact: true }).fill(token); await personal.getByRole('button', { name: '查询我的用量', exact: true }).click();
    await personal.getByRole('heading', { name: '请求明细', exact: true }).waitFor();
    await personal.getByLabel('个人令牌', { exact: true }).fill('');
    await personal.screenshot({ path: path.join(output, clientOnly ? 'narrow-personal-local.png' : 'narrow-personal-https.png'), fullPage: true });
    evidence.personal_browser_transport = clientOnly ? 'local-http' : 'https';
    evidence.stage = 'completed';
})().catch(error => {
    evidence.error = redact(String(error.message), [password, token, secondUser?.token, ...(material?.providers.map(provider => provider.credential) || [])]);
    console.error(evidence.error); process.exitCode = 1;
}).finally(async () => {
    for (const child of [...children]) await stop(child);
    if (interceptor) await new Promise(resolve => interceptor.close(resolve));
    if (browser) await browser.close(); evidence.server_stopped = true;
    try { await trustFixture(launch, 'Restore', output, fingerprint || ''); evidence.trust_restored = true; }
    catch (error) { evidence.trust_restored = false; evidence.cleanup_error = String(error.message).slice(0, 2000); process.exitCode = 1; }
    await fs.writeFile(path.join(output, 'real-upstream-results.json'), JSON.stringify(evidence, null, 2));
});

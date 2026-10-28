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
const dll = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
let server, browser, interceptor, instance, material, token;
const evidence = { started_at: new Date().toISOString(), providers: [], server_stopped: false };
const password = crypto.randomBytes(24).toString('hex');
const children = new Set();
function launch(file, args, options = {}) {
    const child = spawn(file, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], ...options });
    try { os.setPriority(child.pid, os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { }
    children.add(child); child.once('exit', () => children.delete(child)); return child;
}
async function stop(child) {
    if (!child || child.exitCode !== null) return;
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
async function candidate(page, url, provider) {
    const options = provider.options;
    await page.goto(url + '/Upstream'); await page.getByLabel('接入模式', { exact: true }).selectOption(options.mode);
    await page.getByLabel('提供商预设', { exact: true }).selectOption('compatible');
    await page.getByLabel('上游 API 根地址', { exact: true }).fill(options.baseUrl);
    await page.getByLabel('API 模式认证方式', { exact: true }).selectOption(options.authentication);
    await page.getByLabel('上游凭据（账户访问令牌或 API 密钥）', { exact: true }).fill(provider.credential);
    await page.getByLabel('要测试并推荐的模型', { exact: true }).fill(options.model);
    await page.getByLabel('调用检测方式', { exact: true }).selectOption(options.diagnostic);
    await page.getByText('网络与兼容设置', { exact: true }).click();
    await page.getByLabel('出站 HTTP CONNECT 代理（可选）', { exact: true }).fill(options.proxyUrl || '');
    await page.getByRole('button', { name: '保存候选配置', exact: true }).click();
    await page.getByText('候选已保存，尚未改变活动上游。请确认后测试最小调用。', { exact: true }).waitFor();
    assert.ok(!(await page.content()).includes(provider.credential), '候选页面不得回显凭据');
    await page.getByLabel('我同意发起一次最小模型调用，可能消耗少量上游额度').check();
    await page.getByRole('button', { name: '测试候选', exact: true }).click();
    const activate = page.getByRole('button', { name: '激活此候选', exact: true });
    await activate.waitFor({ timeout: 60000 });
    assert.ok(!(await page.content()).includes(provider.credential), '检测页面不得回显凭据');
    await page.screenshot({ path: path.join(output, new URL(options.baseUrl).host + '-tested.png'), fullPage: true });
    await activate.click(); await page.getByText('新上游已激活，后续请求使用新配置；个人令牌和中转地址无需更换。', { exact: true }).waitFor();
}
async function native(url, model, tool = false) {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'HouseholdRelay-rework-client-'));
    if (tool) await fs.writeFile(path.join(directory, 'probe.txt'), 'HOUSEHOLD_REAL_TOOL_OK');
    const env = { ...process.env };
    for (const key of Object.keys(env)) if (/^(ANTHROPIC_|CLAUDE_)/i.test(key) || /PROXY/i.test(key) || ['NODE_EXTRA_CA_CERTS', 'SSL_CERT_FILE', 'NODE_TLS_REJECT_UNAUTHORIZED'].includes(key)) delete env[key];
    Object.assign(env, { ANTHROPIC_BASE_URL: url, ANTHROPIC_AUTH_TOKEN: token, CLAUDE_CONFIG_DIR: path.join(directory, 'config'), CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' });
    const args = ['--safe-mode', '--no-session-persistence', '--strict-mcp-config', '--no-chrome', '--setting-sources', '', '--tools', tool ? 'Read' : '', '--model', model, '--max-turns', tool ? '3' : '1', '--output-format', 'json'];
    if (tool) args.push('--allowedTools', 'Read');
    args.push('--print', tool ? 'Use the Read tool to read probe.txt in the current directory, then reply with its exact content.' : 'Reply with OK.');
    const child = launch(material.claude, args, { cwd: directory, env });
    let stdout = ''; child.stdout.on('data', value => stdout += value.toString()); child.stderr.resume();
    const timer = setTimeout(() => child.kill(), 90000);
    const code = await new Promise(resolve => child.once('exit', resolve)); clearTimeout(timer);
    assert.ok(code === 0, '原生客户端会话失败');
    const result = stdout.split('\n').map(line => { try { return JSON.parse(line); } catch { return null; } }).find(value => value?.type === 'result');
    assert.ok(result && !result.is_error && result.subtype === 'success', '原生客户端未返回成功结果');
    assert.ok(result.result.includes(tool ? 'HOUSEHOLD_REAL_TOOL_OK' : 'OK'), '客户端结果不符合专用测试提示');
    return { exit_code: code, success: true, tool, turns: result.num_turns, usage: result.usage };
}
async function call(url, body, headers, stream) {
    const outgoing = { 'content-type': 'application/json', Authorization: 'Bearer ' + token };
    for (const name of ['user-agent', 'anthropic-beta', 'anthropic-version', 'x-app']) if (headers[name]) outgoing[name] = headers[name];
    const started = Date.now();
    const response = await fetch(url + '/v1/messages?beta=true', { method: 'POST', headers: outgoing, body: JSON.stringify({ ...body, stream }), signal: AbortSignal.timeout(60000) });
    const content = await response.text();
    assert.ok(material.providers.every(provider => !content.includes(provider.credential)), '响应不得泄露上游凭据');
    assert.ok(response.status === 200, '真实转发请求未成功');
    if (stream) {
        const events = [...content.matchAll(/^event: (.+)$/gm)].map(match => match[1]);
        assert.ok(events.includes('message_stop') && !events.includes('error'), 'SSE 必须完整结束');
        return { status: response.status, events, elapsed_ms: Date.now() - started };
    }
    const message = JSON.parse(content); assert.ok(message.type === 'message' && message.role === 'assistant', 'JSON 必须为真实 Messages 回复');
    return { status: response.status, model: message.model, usage: message.usage, elapsed_ms: Date.now() - started };
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
    await page.goto(url + '/Users'); await page.locator('#new-name').fill('真实联调'); await page.getByRole('button', { name: '创建并生成令牌' }).click();
    token = await page.locator('#issued-token').textContent();
    let captured, toolRequests = 0, toolResults = 0;
    interceptor = http.createServer(async (request, response) => {
        try {
            const chunks = []; for await (const chunk of request) chunks.push(chunk); const body = Buffer.concat(chunks);
            if (request.url.startsWith('/v1/messages') && !request.url.includes('count_tokens')) {
                const message = JSON.parse(body); captured = { body: message, headers: request.headers };
                if (message.tools?.some(tool => tool.name === 'Read')) toolRequests++;
                if (message.messages?.some(message => Array.isArray(message.content) && message.content.some(block => block.type === 'tool_result'))) toolResults++;
            }
            const headers = { ...request.headers }; delete headers.host; delete headers['content-length']; delete headers.connection;
            const upstream = await fetch(url + request.url, { method: request.method, headers, body: request.method === 'POST' ? body : undefined, signal: AbortSignal.timeout(90000) });
            response.writeHead(upstream.status, { 'content-type': upstream.headers.get('content-type') || 'application/json' });
            for await (const chunk of upstream.body) response.write(chunk); response.end();
        } catch { response.destroy(); }
    });
    await new Promise(resolve => interceptor.listen(0, '127.0.0.1', resolve)); const clientUrl = `http://127.0.0.1:${interceptor.address().port}`;
    for (const provider of material.providers) {
        const options = provider.options; const item = { base_url: options.baseUrl, authentication: options.authentication, model: options.model, proxy: options.proxyUrl, diagnostic: options.diagnostic };
        evidence.providers.push(item); await candidate(page, url, provider); item.web_test_and_activation = true;
        item.native = await native(clientUrl, options.model); const simple = captured;
        item.client_version = simple.headers['user-agent'];
        item.json = await call(url, simple.body, simple.headers, false); item.sse = await call(url, simple.body, simple.headers, true);
        toolRequests = 0; toolResults = 0; item.tool_session = await native(clientUrl, options.model, true);
        assert.ok(toolRequests > 0 && toolResults > 0, '必须实际发生 Read 和 tool_result 回合'); item.tool_roundtrip = true;
        await page.goto(url + '/Connection'); assert.ok(!(await page.content()).includes(provider.credential) && !(await page.content()).includes(token), '连接说明不能包含秘密');
        if (options.diagnostic === 'claude-code') await page.getByText(/当前上游已用 Claude Code 实际调用验证/).waitFor();
        await page.screenshot({ path: path.join(output, new URL(options.baseUrl).host + '-connection.png'), fullPage: true });
        await stop(server); ({ url } = await start(port)); await page.context().clearCookies(); await login(page, url);
        item.after_restart = await call(url, simple.body, simple.headers, false);
        await fs.writeFile(path.join(output, 'real-upstream-results.json'), JSON.stringify(evidence, null, 2));
        console.log('真实上游完成：' + new URL(options.baseUrl).host + '，网页/JSON/SSE/工具/重启通过');
    }
    await candidate(page, url, material.providers[0]); evidence.switch_back = await native(clientUrl, material.providers[0].options.model);
    await page.setViewportSize({ width: 390, height: 844 }); await page.goto(url + '/Upstream');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), '窄屏不得水平溢出');
    await page.screenshot({ path: path.join(output, 'narrow-upstream.png'), fullPage: true }); evidence.narrow_screen = true;
})().catch(error => {
    evidence.error = String(error.message).split('\n')[0];
    for (const secret of [password, token, ...(material?.providers.map(provider => provider.credential) || [])].filter(Boolean)) evidence.error = evidence.error.split(secret).join('[redacted]');
    console.error(evidence.error); process.exitCode = 1;
}).finally(async () => {
    for (const child of [...children]) await stop(child);
    if (interceptor) await new Promise(resolve => interceptor.close(resolve));
    if (browser) await browser.close(); evidence.server_stopped = true;
    await fs.writeFile(path.join(output, 'real-upstream-results.json'), JSON.stringify(evidence, null, 2));
});

// 合成验收辅助：正常 TLS、一次性信任和有界脱敏诊断，不接触产品业务日志。
const https = require('node:https');
const fs = require('node:fs/promises');
const path = require('node:path');
const { Readable } = require('node:stream');
const assert = require('node:assert/strict');

function redact(text, secrets) {
    let result = String(text);
    for (const secret of secrets.filter(Boolean)) for (const value of [secret, encodeURIComponent(secret), Buffer.from(secret).toString('base64')]) result = result.split(value).join('[redacted]');
    return result.replace(/hr_[A-Za-z0-9_-]+/g, '[personal-token]').slice(0, 4000);
}
function secureFetch(url, options = {}, ca, servername) {
    if (!url.startsWith('https:')) return fetch(url, options);
    return new Promise((resolve, reject) => {
        const request = https.request(url, { method: options.method || 'GET', headers: options.headers, ca, servername, signal: options.signal }, response => {
            const headers = new Headers();
            for (const [name, value] of Object.entries(response.headers)) if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(', ') : value);
            resolve(new Response([204, 205, 304].includes(response.statusCode) ? null : Readable.toWeb(response), { status: response.statusCode, headers }));
        });
        request.once('error', reject); request.end(options.body);
    });
}
async function trustFixture(launch, operation, directory, fingerprint = '', url = '') {
    const child = launch('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', path.join(__dirname, 'private-trust-fixture.ps1'), '-Operation', operation, '-Directory', directory, '-Fingerprint', fingerprint, '-Url', url]);
    let stdout = '', stderr = ''; child.stdout.on('data', chunk => stdout += chunk); child.stderr.on('data', chunk => stderr += chunk);
    // 根信任安装的 Windows 安全确认需要操作者处理；等待期间不跳过系统确认。
    const timer = setTimeout(() => child.kill(), operation === 'Install' ? 600000 : 120000);
    const code = await new Promise(resolve => child.once('exit', resolve)); clearTimeout(timer);
    assert.equal(code, 0, '信任夹具操作失败：' + operation + ' ' + stdout.slice(-1000) + ' ' + stderr.slice(0, 1000));
    return stdout.trim();
}
async function nativeSession({ launch, material, token, url, model, tool, directory, caPath, evidence, output }) {
    if (tool) await fs.writeFile(path.join(directory, 'probe.txt'), 'HOUSEHOLD_REAL_TOOL_OK');
    const env = { ...process.env };
    for (const key of Object.keys(env)) if (/^(ANTHROPIC_|CLAUDE_)/i.test(key) || /PROXY/i.test(key) || ['NODE_EXTRA_CA_CERTS', 'SSL_CERT_FILE', 'NODE_TLS_REJECT_UNAUTHORIZED'].includes(key)) delete env[key];
    Object.assign(env, { ANTHROPIC_BASE_URL: url, ANTHROPIC_AUTH_TOKEN: token, CLAUDE_CONFIG_DIR: path.join(directory, 'config'), CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' });
    if (caPath) env.NODE_EXTRA_CA_CERTS = caPath;
    const args = ['--safe-mode', '--no-session-persistence', '--strict-mcp-config', '--no-chrome', '--setting-sources', '', '--tools', tool ? 'Read' : '', '--model', model, '--max-turns', tool ? '3' : '1', '--output-format', 'stream-json', '--verbose'];
    if (tool) args.push('--allowedTools', 'Read');
    args.push('--print', tool ? 'Use the Read tool to read probe.txt in the current directory, then reply with its exact content.' : 'Reply with OK.');
    const child = launch(material.claude, args, { cwd: directory, env });
    let stdout = '', stderr = '', timedOut = false, oversized = false;
    child.stdout.on('data', chunk => { if (stdout.length + chunk.length <= 1024 * 1024) stdout += chunk; else { oversized = true; child.kill(); } });
    child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-8000); });
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, 90000);
    const code = await new Promise(resolve => child.once('exit', resolve)); clearTimeout(timer);
    const events = stdout.split('\n').map(line => { try { return JSON.parse(line); } catch { return null; } }).filter(Boolean);
    const result = events.find(value => value.type === 'result');
    const blocks = events.flatMap(event => event.message?.content || []);
    const diagnostic = { stage: evidence.stage, tls: url.startsWith('https:'), exit_code: code, signal: child.signalCode, timed_out: timedOut, oversized, tool,
        turns: result?.num_turns, result_subtype: result?.subtype, is_error: result?.is_error,
        tool_names: blocks.filter(block => block.type === 'tool_use').map(block => block.name), tool_results: blocks.filter(block => block.type === 'tool_result').length,
        marker_matched: typeof result?.result === 'string' && result.result.includes(tool ? 'HOUSEHOLD_REAL_TOOL_OK' : 'OK') };
    evidence.client_diagnostics ??= []; evidence.client_diagnostics.push(diagnostic);
    if (code !== 0 || result?.is_error || !diagnostic.marker_matched || (tool && (!diagnostic.tool_names.includes('Read') || diagnostic.tool_results === 0))) {
        const secrets = [token, directory, ...material.providers.map(provider => provider.credential)];
        diagnostic.synthetic_result = redact(result?.result || '', secrets); diagnostic.synthetic_stderr = redact(stderr, secrets);
        diagnostic.event_types = events.map(event => event.type); diagnostic.result_errors = redact(JSON.stringify(result?.errors || []), secrets);
    }
    await fs.writeFile(path.join(output, 'client-diagnostics.json'), JSON.stringify(evidence.client_diagnostics, null, 2));
    assert.ok(code === 0 && !timedOut && !oversized, '原生客户端会话失败');
    assert.ok(result && !result.is_error && result.subtype === 'success', '原生客户端未返回成功结果');
    assert.ok(diagnostic.marker_matched, '客户端结果不符合专用测试提示');
    if (tool) assert.ok(diagnostic.tool_names.includes('Read') && diagnostic.tool_results > 0, '必须实际发生 Read 和 tool_result 回合');
    return { exit_code: code, success: true, tool, turns: result.num_turns, usage: result.usage, tls: diagnostic.tls };
}
module.exports = { redact, secureFetch, trustFixture, nativeSession };

// 对合成客户端失败诊断做受控反例，不使用真实凭据或模型请求。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { nativeSession } = require('./real-validation-support.cjs');
for (const scenario of ['marker', 'missing-tool', 'process-exit']) test('synthetic client failure remains visible and redacted: ' + scenario, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'household-diagnostic-test-'));
    const upstreamSecret = 'synthetic-upstream-secret-PRIVATE', token = 'hr_synthetic-personal-PRIVATE';
    const result = { type: 'result', subtype: 'success', is_error: false, result: scenario === 'missing-tool' ? 'HOUSEHOLD_REAL_TOOL_OK' : upstreamSecret + ' ' + token, num_turns: 1 };
    const fixture = `process.stdout.write(${JSON.stringify(JSON.stringify(result) + '\n')}); process.stderr.write(${JSON.stringify(encodeURIComponent(upstreamSecret) + ' ' + Buffer.from(upstreamSecret).toString('base64'))}); process.exitCode = ${scenario === 'process-exit' ? 6 : 0};`;
    const evidence = { stage: 'controlled-' + scenario };
    try {
        const launch = (_file, _args, options) => spawn(process.execPath, ['-e', fixture], { ...options, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
        await assert.rejects(() => nativeSession({ launch, material: { claude: 'fixture', providers: [{ credential: upstreamSecret }] }, token,
            url: 'https://localhost', model: 'fixture', tool: scenario === 'missing-tool', directory, caPath: null, evidence, output: directory }));
        const saved = await fs.readFile(path.join(directory, 'client-diagnostics.json'), 'utf8');
        for (const secret of [upstreamSecret, token, Buffer.from(upstreamSecret).toString('base64')]) assert.ok(!saved.includes(secret));
        const diagnostic = JSON.parse(saved)[0]; assert.equal(diagnostic.stage, evidence.stage);
        assert.equal(diagnostic.exit_code, scenario === 'process-exit' ? 6 : 0);
        assert.equal(diagnostic.marker_matched, scenario === 'missing-tool'); assert.equal(diagnostic.tool_results, 0);
        assert.ok(diagnostic.synthetic_stderr.includes('[redacted]'));
    } finally {
        assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
        assert.ok(path.basename(directory).startsWith('household-diagnostic-test-')); await fs.rm(directory, { recursive: true });
    }
});

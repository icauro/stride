const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

async function main() {
    const root = path.resolve(__dirname, 'ManagedWebGpuProbe/bin/Release/net11.0/publish/wwwroot');
    const mime = { '.js': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.html': 'text/html' };
    const server = http.createServer(async (request, response) => {
        try {
            const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
            if (pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
            const file = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
            const relative = path.relative(root, file);
            if (relative.startsWith('..') || path.isAbsolute(relative)) { response.writeHead(403).end(); return; }
            const bytes = await fs.readFile(file);
            response.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' }).end(bytes);
        } catch { response.writeHead(404).end(); }
    });
    let browser;
    try {
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_EXECUTABLE });
        const page = await browser.newPage();
        const unexpected = [], expected = [];
        page.on('pageerror', error => unexpected.push(String(error)));
        page.on('console', message => {
            if (message.type() !== 'error' && message.type() !== 'warning') return;
            const text = message.text();
            // Only the deliberately injected shader diagnostic is expected.
            if (text.includes('missing_callback_function') && text.includes('Stride_callback_ΩЖ') && text.includes('Error while parsing WGSL')) expected.push(text);
            else unexpected.push(text);
        });
        for (let cycle = 0; cycle < 3; cycle++) {
            if (cycle === 0) await page.goto(`http://127.0.0.1:${server.address().port}/?error-callbacks`);
            else await page.reload();
            await page.waitForFunction(() => ['passed', 'failed'].includes(globalThis.smokeResult?.status), null, { timeout: 120000 });
            const result = await page.evaluate(() => globalThis.smokeResult);
            console.log(JSON.stringify({ browser: browser.version(), cycle, result, unexpected, expectedCount: expected.length }, null, 2));
            assert.equal(result.status, 'passed');
            assert.equal(result.errorCallbacksVerified, true);
            assert.match(result.gpuErrorMessage, /^WebGPU Validation:/);
            assert.ok(result.gpuErrorMessage.includes('Stride_callback_ΩЖ'));
            assert.ok(result.gpuErrorMessage.includes('missing_callback_function'));
            assert.deepEqual(unexpected, []);
            assert.equal(expected.length, cycle + 1);
        }
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

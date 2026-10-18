const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');
const { PNG } = require('pngjs');

async function main() {
    const root = path.resolve(__dirname, 'SpaceEscape.Browser/bin/Release/net11.0/publish/wwwroot');
    const mime = { '.js': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.html': 'text/html' };
    const server = http.createServer(async (request, response) => {
        try {
            const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
            if (pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
            const file = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
            const relative = path.relative(root, file);
            if (relative.startsWith('..') || path.isAbsolute(relative)) { response.writeHead(403).end(); return; }
            response.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
            response.end(await fs.readFile(file));
        } catch { response.writeHead(404).end(); }
    });
    let browser;
    try {
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_EXECUTABLE });
        const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
        const errors = [];
        const messages = [];
        page.on('pageerror', error => errors.push(String(error)));
        page.on('console', message => {
            messages.push(message.text());
            if (message.type() === 'error') errors.push(message.text());
        });
        await page.goto(`http://127.0.0.1:${server.address().port}/`);
        await page.waitForFunction(() => ['running', 'failed'].includes(globalThis.spaceEscapeResult?.status), { timeout: 120000 });
        let result = await page.evaluate(() => globalThis.spaceEscapeResult);
        console.log(JSON.stringify({ browser: browser.version(), result, errors }, null, 2));
        assert.equal(result.status, 'running', 'Space Escape startup failed');
        await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' ||
            (globalThis.spaceEscapeResult.frames >= 60 && globalThis.spaceEscapeResult.entities > 0), { timeout: 60000 });
        result = await page.evaluate(() => globalThis.spaceEscapeResult);
        console.log(JSON.stringify({ result, errors }, null, 2));
        assert.equal(result.status, 'running', 'Space Escape frame loop failed');
        assert.ok(result.frames >= 60);
        assert.ok(result.entities > 0, 'The sample scene has not replaced the splash screen');
        const settledFrame = result.frames + 60;
        await page.waitForFunction(target => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.frames >= target,
            settledFrame, { timeout: 30000 });
        result = await page.evaluate(() => globalThis.spaceEscapeResult);
        console.log(JSON.stringify({ result, errors, messages: messages.slice(-50) }, null, 2));
        assert.equal(result.status, 'running');
        const png = PNG.sync.read(await page.locator('#game').screenshot());
        if (process.env.BROWSER_CAPTURE_PATH) await page.locator('#game').screenshot({ path: process.env.BROWSER_CAPTURE_PATH });
        let nonBlack = 0;
        for (let i = 0; i < png.data.length; i += 4)
            if (png.data[i] + png.data[i + 1] + png.data[i + 2] > 24) nonBlack++;
        console.log(JSON.stringify({ nonBlack, total: png.width * png.height }));
        assert.ok(nonBlack > png.width * png.height / 10, 'Actual game canvas is mostly black');
        // This is a startup/rendering gate. Gameplay/button/lane assertions follow
        // once actual sample frames pass; do not interpret this as full completion.
        await page.evaluate(() => globalThis.stopSpaceEscape());
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ renderedPixels: nonBlack, totalPixels: png.width * png.height, stopped: true }));
    } finally {
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

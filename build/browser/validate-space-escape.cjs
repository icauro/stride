const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');
const { PNG } = require('pngjs');

async function main() {
    const touch = process.argv.includes('--touch');
    const project = path.join(__dirname, 'SpaceEscape.Browser');
    const approved = JSON.parse(await fs.readFile(path.join(__dirname, 'ManagedWebGpuProbe/core-imports.json'), 'utf8'));
    const table = await fs.readFile(path.join(project, 'obj/Release/net11.0/wasm/for-publish/pinvoke-table.h'), 'utf8');
    const imports = table.match(/static PinvokeImport wgpu_native_imports \[\] = \{([\s\S]*?)\n\};/);
    assert.ok(imports);
    const actual = [...imports[1].matchAll(/\{"(wgpu\w+)"/g)].map(match => match[1]);
    assert.ok(actual.length > 0);
    assert.deepEqual(actual.filter(name => !approved.approvedCoreImports.includes(name)), [], 'Sample imports an unaudited/native-only WebGPU entry point');
    console.log(JSON.stringify({ approvedSampleImports: actual.length, touch }));
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
    let browser, page;
    const captured = new Map();
    try {
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_EXECUTABLE });
        const browserContext = await browser.newContext({ viewport: { width: 1100, height: 800 }, hasTouch: touch });
        page = await browserContext.newPage();
        const cdp = await page.context().newCDPSession(page);
        async function tap(bounds, x, y) {
            if (touch) await page.touchscreen.tap(bounds.x + bounds.width * x, bounds.y + bounds.height * y);
            else await page.locator('#game').click({ position: { x: bounds.width * x, y: bounds.height * y } });
        }
        async function swipe(bounds, dx, dy) {
            const x = bounds.x + bounds.width * .5, y = bounds.y + bounds.height * .5;
            const point = (fx, fy) => [{ x: fx, y: fy, id: 7 }];
            await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: point(x, y) });
            await page.evaluate(() => new Promise(requestAnimationFrame));
            for (let step = 1; step <= 4; step++) {
                await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: point(x + dx * bounds.width * step / 4, y + dy * bounds.height * step / 4) });
                await page.evaluate(() => new Promise(requestAnimationFrame));
            }
            await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
        }
        const errors = [];
        const messages = [];
        page.on('pageerror', error => errors.push(String(error)));
        page.on('console', message => {
            messages.push(message.text());
            if (message.type() === 'error') errors.push(message.text());
        });
        for (let cycle = 0; cycle < 2; cycle++) {
            errors.length = 0;
            messages.length = 0;
            if (cycle === 0) await page.goto(`http://127.0.0.1:${server.address().port}/`);
            else await page.reload();
            await page.waitForFunction(() => ['running', 'failed'].includes(globalThis.spaceEscapeResult?.status), { timeout: 120000 });
            let result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ browser: browser.version(), cycle, result, errors }, null, 2));
            assert.equal(result.status, 'running', 'Space Escape startup failed');
            const wasm = await page.evaluate(async () => {
                const urls = [...new Set(performance.getEntriesByType('resource').map(entry => entry.name)
                    .filter(name => /\/dotnet\.native(?:\.[^/]+)?\.wasm(?:\?|$)/.test(name)))];
                if (urls.length !== 1) throw new Error(`Expected one active native WASM module, found ${urls.length}`);
                return { url: urls[0], valid: WebAssembly.validate(await (await fetch(urls[0])).arrayBuffer()) };
            });
            console.log(JSON.stringify({ cycle, wasm }));
            assert.ok(wasm.valid, 'The exact active native WASM module failed full validation');
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
            if (cycle === 0 && process.env.BROWSER_CAPTURE_PATH) await page.locator('#game').screenshot({ path: process.env.BROWSER_CAPTURE_PATH });
            let nonBlack = 0;
            for (let i = 0; i < png.data.length; i += 4)
                if (png.data[i] + png.data[i + 1] + png.data[i + 2] > 24) nonBlack++;
            console.log(JSON.stringify({ nonBlack, total: png.width * png.height }));
            assert.ok(nonBlack > png.width * png.height / 10, 'Actual game canvas is mostly black');
            assert.equal(result.ui, 'Touch to Start', 'The original main menu did not initialize');
            const bounds = await page.locator('#game').boundingBox();
            await tap(bounds, .5, .8);
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.ui.startsWith('Distance :'),
                null, { timeout: 10000 });
            assert.equal((await page.evaluate(() => globalThis.spaceEscapeResult)).status, 'running');
            if (touch) await swipe(bounds, .18, 0);
            else await page.keyboard.down('ArrowRight');
            await page.waitForFunction(() => globalThis.spaceEscapeResult.dead || globalThis.spaceEscapeResult.status === 'failed' ||
                (globalThis.spaceEscapeResult.lane === 2 && Math.abs(globalThis.spaceEscapeResult.characterX + 5) < .001), null, { timeout: 10000 });
            if (!touch) await page.keyboard.up('ArrowRight');
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ afterRight: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.equal(result.lane, 2);
            assert.ok(Math.abs(result.characterX + 5) < .001, 'The right arrow did not move the ship to the right lane');
            if (touch) await swipe(bounds, -.18, 0);
            else await page.keyboard.down('ArrowLeft');
            await page.waitForFunction(() => globalThis.spaceEscapeResult.dead || globalThis.spaceEscapeResult.status === 'failed' ||
                (globalThis.spaceEscapeResult.lane === 1 && Math.abs(globalThis.spaceEscapeResult.characterX) < .001), null, { timeout: 10000 });
            if (!touch) await page.keyboard.up('ArrowLeft');
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ afterLeft: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.equal(result.lane, 1);
            assert.ok(Math.abs(result.characterX) < .2, 'The left arrow did not return the ship to the middle lane');
            await page.waitForFunction(() => globalThis.spaceEscapeResult.dead || globalThis.spaceEscapeResult.status === 'failed' ||
                Number(globalThis.spaceEscapeResult.ui.split(':')[1]) >= 1, null, { timeout: 10000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ gameplay: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.ok(Number(result.ui.split(':')[1]) >= 1, 'The running game did not advance its distance score');
            const standingHeight = result.hitboxHeight;
            assert.ok(standingHeight > 0);
            if (touch) await swipe(bounds, 0, .18);
            else await page.keyboard.down('ArrowDown');
            await page.waitForFunction(height => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.dead ||
                globalThis.spaceEscapeResult.hitboxHeight < height - .5, standingHeight, { timeout: 10000 });
            if (!touch) await page.keyboard.up('ArrowDown');
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ sliding: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.ok(result.hitboxHeight < standingHeight - .5, 'The down arrow did not activate the slide hitbox');
            if (cycle === 0 && process.env.BROWSER_CAPTURE_PATH) await page.locator('#game').screenshot({ path: process.env.BROWSER_CAPTURE_PATH.replace(/\.png$/i, '-playing.png') });
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.dead,
                null, { timeout: 30000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ gameOver: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, true);
            assert.ok(result.ui.includes('Retry') && result.ui.includes('Menu'));
            await tap(bounds, .7, .7);
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.ui === 'Touch to Start',
                null, { timeout: 10000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.equal(result.lane, 1);
            await tap(bounds, .5, .8);
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.ui.startsWith('Distance :'),
                null, { timeout: 10000 });
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.dead,
                null, { timeout: 30000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            assert.equal(result.status, 'running');
            assert.equal(result.dead, true);
            await tap(bounds, .3, .7);
            await page.waitForFunction(() => globalThis.spaceEscapeResult.status === 'failed' ||
                (!globalThis.spaceEscapeResult.dead && globalThis.spaceEscapeResult.ui.startsWith('Distance :')),
                null, { timeout: 10000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ retry: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            assert.equal(result.dead, false);
            assert.equal(result.lane, 1);
            await page.evaluate(() => { const canvas = document.getElementById('game'); canvas.width = 800; canvas.height = 1420; });
            const resizedFrame = result.frames + 10;
            await page.waitForFunction(target => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.frames >= target, resizedFrame, { timeout: 10000 });
            result = await page.evaluate(() => globalThis.spaceEscapeResult);
            console.log(JSON.stringify({ resized: result, errors }, null, 2));
            assert.equal(result.status, 'running');
            const resizedPixels = PNG.sync.read(await page.locator('#game').screenshot());
            let resizedNonBlack = 0;
            for (let i = 0; i < resizedPixels.data.length; i += 4)
                if (resizedPixels.data[i] + resizedPixels.data[i + 1] + resizedPixels.data[i + 2] > 24) resizedNonBlack++;
            assert.ok(resizedNonBlack > resizedPixels.width * resizedPixels.height / 10, 'The game stopped rendering after its GPU backbuffer resized');
            await page.setViewportSize({ width: 400, height: 700 });
            await page.evaluate(() => { const canvas = document.getElementById('game'); canvas.width = 640; canvas.height = 1136; });
            const otherPage = await page.context().newPage();
            // Playwright forces every page to appear focused by default. Disable
            // that override so this checks real focus loss rather than a fake event.
            await cdp.send('Emulation.setFocusEmulationEnabled', { enabled: false });
            await otherPage.goto('about:blank');
            await otherPage.bringToFront();
            assert.equal(await page.evaluate(() => document.hasFocus()), false, 'The focus-loss check did not actually blur the game page');
            await otherPage.close();
            await page.bringToFront();
            const resumedFrame = result.frames + 10;
            await page.waitForFunction(target => globalThis.spaceEscapeResult.status === 'failed' || globalThis.spaceEscapeResult.frames >= target, resumedFrame, { timeout: 10000 });
            assert.equal((await page.evaluate(() => globalThis.spaceEscapeResult)).status, 'running');
            await page.evaluate(() => globalThis.stopSpaceEscape());
            assert.equal(await page.evaluate(() => globalThis.spaceEscapeResult.status), 'stopped');
            assert.deepEqual(errors, []);
            for (const request of await page.evaluate(() => globalThis.spaceEscapeRequests || [])) captured.set(request.hash, request);
            console.log(JSON.stringify({ cycle, renderedPixels: nonBlack, totalPixels: png.width * png.height, stopped: true, input: touch ? 'touch' : 'keyboard/mouse' }));
            await page.setViewportSize({ width: 1100, height: 800 });
        }
    } finally {
        if (page && process.env.BROWSER_EFFECT_REQUESTS_PATH) {
            for (const request of await page.evaluate(() => globalThis.spaceEscapeRequests || [])) captured.set(request.hash, request);
            const requests = [...captured.values()].sort((a, b) => a.hash.localeCompare(b.hash));
            await fs.writeFile(process.env.BROWSER_EFFECT_REQUESTS_PATH, JSON.stringify(requests, null, 2));
            console.log(`Captured ${requests.length} resolved effect request(s).`);
        }
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

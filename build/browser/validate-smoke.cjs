const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

// Serve only the publish tree on loopback. A fresh browser context exercises the
// actual WASM download/runtime, with no dependency on a developer's browser tabs.
const cWebgpu = process.argv.includes('--c-webgpu');
const managedWebgpu = process.argv.includes('--managed-webgpu');
const webgpu = cWebgpu || managedWebgpu || process.argv.includes('--webgpu');
const root = process.env.BROWSER_PUBLISH_ROOT
    ? path.resolve(process.env.BROWSER_PUBLISH_ROOT)
    : path.resolve(__dirname, `${managedWebgpu ? 'ManagedWebGpuProbe' : cWebgpu ? 'CWebGpuProbe' : webgpu ? 'WebGpuProbe' : 'Smoke'}/bin/Release/net11.0/publish/wwwroot`);
const mime = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json',
    '.wasm': 'application/wasm'
};

async function verifyManagedImports() {
    const project = path.join(__dirname, 'ManagedWebGpuProbe');
    const approved = JSON.parse(await fs.readFile(path.join(project, 'core-imports.json'), 'utf8'));
    const table = await fs.readFile(path.join(project, 'obj/Release/net11.0/wasm/for-publish/pinvoke-table.h'), 'utf8');
    const block = table.match(/static PinvokeImport wgpu_native_imports \[\] = \{([\s\S]*?)\n\};/);
    assert.ok(block, 'Published native import table lacks the WebGPU static module');
    const actual = [...block[1].matchAll(/\{"(wgpu\w+)"/g)].map(match => match[1]).sort();
    assert.deepEqual(actual, [...approved.approvedCoreImports].sort(),
        'WebGPU import surface changed; audit signatures against both pinned headers before updating approval');
    const projectSource = await fs.readFile(path.join(project, 'ManagedWebGpuProbe.csproj'), 'utf8');
    assert.doesNotMatch(projectSource, /<NativeFileReference\b/, 'Managed proof must not link a handwritten native helper');
    return { count: actual.length, provider: approved.provider };
}

async function main() {
    await fs.access(path.join(root, 'index.html'));
    const managedImports = managedWebgpu ? await verifyManagedImports() : undefined;
    const server = http.createServer(async (request, response) => {
        try {
            const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
            if (pathname === '/favicon.ico') {
                response.writeHead(204).end();
                return;
            }
            const file = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
            const relative = path.relative(root, file);
            if (relative.startsWith('..') || path.isAbsolute(relative)) {
                response.writeHead(403).end();
                return;
            }
            const data = await fs.readFile(file);
            response.writeHead(200, {
                'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream',
                'Cache-Control': 'no-store'
            }).end(data);
        } catch {
            response.writeHead(404).end();
        }
    });
    let browser;
    try {
        await new Promise((resolve, reject) => {
            server.once('error', reject);
            server.listen(0, '127.0.0.1', resolve);
        });
        browser = await chromium.launch({
            headless: true,
            ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {})
        });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(String(error)));
        page.on('console', message => {
            if (message.type() === 'error') errors.push(message.text());
        });
        page.on('requestfailed', request => errors.push(`${request.url()}: ${request.failure()?.errorText}`));
        page.on('response', response => {
            if (!response.ok()) errors.push(`HTTP ${response.status()}: ${response.url()}`);
        });
        const url = `http://127.0.0.1:${server.address().port}/`;
        const runs = [];
        for (let run = 0; run < 2; run++) {
            if (run === 0) await page.goto(url);
            else await page.reload();
            await page.waitForFunction(() => ['passed', 'failed'].includes(globalThis.smokeResult?.status), null, { timeout: 60000 });
            const result = await page.evaluate(() => globalThis.smokeResult);
            assert.equal(result.status, 'passed', JSON.stringify(result));
            if (webgpu) {
                if (cWebgpu) assert.equal(result.transport, 'emdawn-c-api');
                if (managedWebgpu) {
                    assert.equal(result.transport, 'generated-csharp');
                    assert.equal(result.pendingCallbacks, 0);
                    assert.equal(result.nativeLoaderRejected, true);
                    assert.equal(result.sharedContextVerified, true);
                }
                assert.equal(result.managedPixelsVerified, true);
                assert.equal(result.invalidMapRejected, true);
                assert.equal(result.disposed, true);
                assert.equal(result.liveResources, 0);
                assert.equal(result.frames.length, 3);
                assert.equal(result.readback.bytesPerRow, 512);
                assert.equal(result.readback.packedBytes, 65 * 63 * 4);
            } else {
                assert.match(result.framework, /^\.NET 11\./);
                assert.equal(result.sum, 42);
            }
            assert.equal(await page.locator('#result').getAttribute('data-status'), 'passed');
            runs.push(result);
        }
        assert.deepEqual(errors, [], 'Browser errors occurred');
        let unavailable;
        if (webgpu) {
            await page.goto(`${url}?no-adapter`);
            await page.waitForFunction(() => globalThis.smokeResult?.status === 'failed', null, { timeout: 60000 });
            unavailable = await page.evaluate(() => globalThis.smokeResult);
            assert.match(unavailable.error, /Injected adapter unavailable/);
            assert.equal(unavailable.frames.length, 0);
            assert.equal(unavailable.managedPixelsVerified, undefined);
        }
        console.log(JSON.stringify({ status: 'passed', browser: browser.version(), managedImports, runs, unavailable }, null, 2));
    } finally {
        try {
            if (browser) await browser.close();
        } finally {
            await new Promise(resolve => server.close(resolve));
        }
    }
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});

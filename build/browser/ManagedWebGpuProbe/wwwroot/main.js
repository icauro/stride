import { dotnet } from './_framework/dotnet.js';

const result = globalThis.smokeResult = { status: 'running', transport: 'generated-csharp', frames: [] };
const output = document.getElementById('result');
const noAdapter = new URLSearchParams(location.search).has('no-adapter');
const verifyCanvas = new URLSearchParams(location.search).has('verify-canvas');
let continueCanvas;
const canvasGate = new Promise(resolve => { continueCanvas = resolve; });
globalThis.continueAfterCanvasChecks = () => continueCanvas();
try {
    const runtime = await dotnet.create();
    runtime.setModuleImports('managed-gpu-probe', {
        noAdapter: () => noAdapter,
        resizeCanvas(selector, width, height) {
            const canvas = document.querySelector(selector);
            canvas.width = width;
            canvas.height = height;
        },
        canvasReady: () => { result.canvasReady = true; },
        waitForCanvasChecks: () => verifyCanvas ? canvasGate : Promise.resolve(),
        effectBaseUrl: () => new URL('./effects/', document.baseURI).href,
        reportEffects: (pixels, values) => {
            result.wgslEffectsVerified = true;
            result.wgslPixels = pixels;
            result.wgslComputeValues = values;
        },
        reportContext: () => { result.sharedContextVerified = true; },
        reportGameLoop: () => { result.gameLoopVerified = true; result.gameLoopCycles = 2; },
        contentBaseUrl: () => new URL('./content/', document.baseURI).href,
        reportAssets: () => { result.contentDatabaseVerified = true; result.contentAssets = 3; },
        reportManagedError: error => { result.managedError = error; },
        reportEngineDevice: (clearFrames, verifiedPixels) => {
            result.engineDeviceVerified = true;
            result.engineClearFrames = clearFrames;
            result.engineVerifiedPixels = verifiedPixels;
        },
        nextFrame: () => new Promise(resolve => requestAnimationFrame(() => resolve())),
        reportFrame(iteration, foreground, background, invalidMap, elapsedMilliseconds) {
            result.frames.push({ iteration, foreground, background, elapsedMilliseconds });
            result.readback = { width: 65, height: 63, bytesPerRow: 512, packedBytes: 16380 };
            result.invalidMapRejected ||= invalidMap;
        },
        reportSuccess() {
            result.managedPixelsVerified = true;
            result.disposed = true;
            result.liveResources = 0;
            result.pendingCallbacks = 0;
            result.nativeLoaderRejected = true;
        }
    });
    await runtime.runMain();
    if (!result.managedPixelsVerified || !result.invalidMapRejected || !result.sharedContextVerified || !result.engineDeviceVerified || !result.wgslEffectsVerified || result.frames.length !== 3)
        throw new Error('Managed binding verification did not complete.');
    result.status = 'passed';
} catch (error) {
    result.status = 'failed';
    result.error = String(error?.stack ?? error);
    if (!noAdapter) console.error(error);
}
output.dataset.status = result.status;
output.textContent = JSON.stringify(result, null, 2);

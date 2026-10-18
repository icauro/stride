import { dotnet } from './_framework/dotnet.js';
import { attachBrowserInput } from './browser-input.js';

const result = globalThis.smokeResult = { status: 'running', transport: 'generated-csharp', frames: [] };
const output = document.getElementById('result');
const noAdapter = new URLSearchParams(location.search).has('no-adapter');
const errorCallbacksOnly = new URLSearchParams(location.search).has('error-callbacks');
const verifyCanvas = !new URLSearchParams(location.search).has('run-tests');
let continueCanvas;
const canvasGate = new Promise(resolve => { continueCanvas = resolve; });
globalThis.continueAfterCanvasChecks = () => continueCanvas();
document.getElementById('run-checks').addEventListener('click', () => continueCanvas());
try {
    const runtime = await dotnet.create();
    const config = runtime.getConfig();
    const managed = (await runtime.getAssemblyExports(config.mainAssemblyName)).BrowserInputCheck;
    let detachInput;
    runtime.setModuleImports('managed-gpu-probe', {
        noAdapter: () => noAdapter,
        errorCallbacksOnly: () => errorCallbacksOnly,
        reportErrorCallbacks: message => { result.errorCallbacksVerified = true; result.gpuErrorMessage = message; },
        resizeCanvas(selector, width, height) {
            const canvas = document.querySelector(selector);
            canvas.width = width;
            canvas.height = height;
        },
        canvasReady: () => {
            result.canvasReady = true;
            output.textContent = 'Rendering checks are visible. Space Escape integration is still in progress.';
        },
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
        reportGameStartup: () => { result.fullGameStartupVerified = true; },
        inputRequested: () => new URLSearchParams(location.search).has('verify-canvas'),
        attachInputAndWait: () => new Promise(resolve => {
            detachInput = attachBrowserInput(document.getElementById('input-canvas'), {
                key: managed.Key, move: managed.Move, button: managed.Button, wheel: managed.Wheel,
                pointer: managed.Pointer, surface: managed.Surface, pause: managed.Pause, cancelMouse: managed.CancelMouse
            });
            globalThis.inputProbe = managed;
            globalThis.finishInputChecks = resolve;
            result.inputReady = true;
        }),
        detachInput: () => { detachInput?.(); globalThis.inputProbe = null; result.inputDetached = true; },
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
    if (errorCallbacksOnly ? !result.errorCallbacksVerified :
        (!result.managedPixelsVerified || !result.invalidMapRejected || !result.sharedContextVerified || !result.engineDeviceVerified || !result.wgslEffectsVerified || result.frames.length !== 3))
        throw new Error('Managed binding verification did not complete.');
    result.status = 'passed';
} catch (error) {
    result.status = 'failed';
    result.error = String(error?.stack ?? error);
    if (!noAdapter) console.error(error);
}
output.dataset.status = result.status;
output.textContent = JSON.stringify(result, null, 2);

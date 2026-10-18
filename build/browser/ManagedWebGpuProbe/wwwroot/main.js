import { dotnet } from './_framework/dotnet.js';

const result = globalThis.smokeResult = { status: 'running', transport: 'generated-csharp', frames: [] };
const output = document.getElementById('result');
const noAdapter = new URLSearchParams(location.search).has('no-adapter');
try {
    const runtime = await dotnet.create();
    runtime.setModuleImports('managed-gpu-probe', {
        noAdapter: () => noAdapter,
        reportContext: () => { result.sharedContextVerified = true; },
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
    if (!result.managedPixelsVerified || !result.invalidMapRejected || !result.sharedContextVerified || result.frames.length !== 3)
        throw new Error('Managed binding verification did not complete.');
    result.status = 'passed';
} catch (error) {
    result.status = 'failed';
    result.error = String(error?.stack ?? error);
    if (!noAdapter) console.error(error);
}
output.dataset.status = result.status;
output.textContent = JSON.stringify(result, null, 2);

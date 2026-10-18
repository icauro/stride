import { dotnet } from './_framework/dotnet.js';
import { createTransport } from './gpu.js';

const output = document.getElementById('result');
const result = globalThis.smokeResult = { status: 'running', frames: [] };
try {
    const runtime = await dotnet.create();
    const parameters = new URLSearchParams(location.search);
    runtime.setModuleImports('gpu-probe', createTransport(result, { noAdapter: parameters.has('no-adapter') }));
    await runtime.runMain();
    if (!result.managedPixelsVerified || !result.disposed || !result.invalidMapRejected || result.frames.length !== 3)
        throw new Error('Managed verification, failure handling or disposal did not complete.');
    result.status = 'passed';
} catch (error) {
    result.status = 'failed';
    result.error = String(error?.stack ?? error);
    if (!new URLSearchParams(location.search).has('no-adapter')) console.error(error);
}
output.dataset.status = result.status;
output.textContent = JSON.stringify(result, null, 2);

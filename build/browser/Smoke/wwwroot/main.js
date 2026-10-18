import { dotnet } from './_framework/dotnet.js';

const output = document.getElementById('result');
globalThis.smokeResult = { status: 'running' };

try {
    let framework;
    let core;
    let imageDecoded = false;
    let windowVerified = false;
    const runtime = await dotnet.create();
    runtime.setModuleImports('smoke', {
        reportStartup: value => { framework = value; },
        reportCore: (platform, assetDirectory) => { core = { platform, assetDirectory, filesystemRoundtrip: true }; },
        reportImage: () => { imageDecoded = true; },
        reportWindow: () => { windowVerified = true; }
    });
    await runtime.runMain();
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const sum = exports.Program.Add(19, 23);
    if (!framework?.startsWith('.NET 11.') || sum !== 42 || core?.platform !== 'Browser' || !imageDecoded || !windowVerified)
        throw new Error(`Unexpected runtime/interop result: ${framework}, ${sum}`);

    globalThis.smokeResult = { status: 'passed', framework, sum, core, imageDecoded, windowVerified };
    output.dataset.status = 'passed';
    output.textContent = JSON.stringify(globalThis.smokeResult, null, 2);
} catch (error) {
    globalThis.smokeResult = { status: 'failed', error: String(error?.stack ?? error) };
    output.dataset.status = 'failed';
    output.textContent = JSON.stringify(globalThis.smokeResult, null, 2);
    console.error(error);
}

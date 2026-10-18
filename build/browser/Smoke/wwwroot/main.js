import { dotnet } from './_framework/dotnet.js';

const output = document.getElementById('result');
globalThis.smokeResult = { status: 'running' };

try {
    let framework;
    const runtime = await dotnet.create();
    runtime.setModuleImports('smoke', {
        reportStartup: value => { framework = value; }
    });
    await runtime.runMain();
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const sum = exports.Program.Add(19, 23);
    if (!framework?.startsWith('.NET 11.') || sum !== 42)
        throw new Error(`Unexpected runtime/interop result: ${framework}, ${sum}`);

    globalThis.smokeResult = { status: 'passed', framework, sum };
    output.dataset.status = 'passed';
    output.textContent = JSON.stringify(globalThis.smokeResult, null, 2);
} catch (error) {
    globalThis.smokeResult = { status: 'failed', error: String(error?.stack ?? error) };
    output.dataset.status = 'failed';
    output.textContent = JSON.stringify(globalThis.smokeResult, null, 2);
    console.error(error);
}

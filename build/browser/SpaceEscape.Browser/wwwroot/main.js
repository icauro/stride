import { dotnet } from './_framework/dotnet.js';
import { attachBrowserInput } from './browser-input.js';

const canvas = document.getElementById('game');
const status = document.getElementById('status');
const result = globalThis.spaceEscapeResult = { status: 'loading', frames: 0 };
let managed, detach, stopped = false;
const fail = error => {
    stopped = true;
    detach?.();
    result.status = 'failed';
    result.error = String(error?.stack || error);
    status.textContent = result.error;
    console.error(error);
};
try {
    const runtime = await dotnet.create();
    runtime.setModuleImports('space-escape', {
        managedError: error => { result.managedError = error; },
        log: (message, level) => { if (level >= 4) console.error(message); else console.log(message); }
    });
    const config = runtime.getConfig();
    managed = (await runtime.getAssemblyExports(config.mainAssemblyName)).Program;
    await runtime.runMain();
    await managed.Start(new URL('content/manifest.json', document.baseURI).href, canvas.width, canvas.height);
    detach = attachBrowserInput(canvas, {
        key: managed.Key, move: managed.Move, button: managed.Button, wheel: managed.Wheel,
        pointer: managed.Pointer, surface: managed.Surface, pause: managed.Pause, cancelMouse: managed.CancelMouse
    });
    result.status = 'running';
    status.textContent = 'Space Escape is running. Click the canvas to play.';
    const frame = () => {
        if (stopped) return;
        try {
            managed.Frame(canvas.width, canvas.height, document.hasFocus(), !document.hidden);
            result.frames = managed.Frames();
            result.entities = managed.Entities();
            requestAnimationFrame(frame);
        } catch (error) { fail(error); }
    };
    requestAnimationFrame(frame);
    globalThis.stopSpaceEscape = async () => { stopped = true; detach?.(); await managed.Stop(); result.status = 'stopped'; };
} catch (error) { fail(error); }

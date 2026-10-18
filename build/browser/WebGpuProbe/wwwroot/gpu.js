// Transport experiment only: these JS objects are not the Stride resource model.
export function createTransport(result, options = {}) {
    const wgsl = `
@vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {
    let positions = array<vec2f, 3>(vec2f(-0.8, -0.8), vec2f(0.8, -0.8), vec2f(0.0, 0.8));
    return vec4f(positions[index], 0.0, 1.0);
}
@fragment fn fs() -> @location(0) vec4f { return vec4f(1.0, 0.0, 0.0, 1.0); }
`;
    return {
        async createContext() {
            if (!globalThis.isSecureContext || !navigator.gpu)
                throw new Error('WebGPU requires a secure context and navigator.gpu.');
            if (options.noAdapter) throw new Error('Injected adapter unavailable');
            const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
            if (!adapter) throw new Error('WebGPU returned no adapter.');
            const device = await adapter.requestDevice();
            const errors = [];
            device.addEventListener('uncapturederror', event => errors.push(event.error.message));
            const state = { device, errors, disposed: false, liveResources: 0 };
            device.lost.then(info => {
                if (!state.disposed) errors.push(`Device lost: ${info.reason}: ${info.message}`);
            });
            try {
                const module = device.createShaderModule({ code: wgsl });
                const compilation = await module.getCompilationInfo();
                const shaderErrors = compilation.messages.filter(message => message.type === 'error');
                if (shaderErrors.length) throw new Error(shaderErrors.map(message => message.message).join('\n'));
                state.pipeline = await device.createRenderPipelineAsync({
                    layout: 'auto',
                    vertex: { module, entryPoint: 'vs' },
                    fragment: { module, entryPoint: 'fs', targets: [{ format: 'rgba8unorm' }] },
                    primitive: { topology: 'triangle-list' }
                });
                state.canvas = document.getElementById('canvas').getContext('webgpu');
                if (!state.canvas) throw new Error('Canvas WebGPU context unavailable.');
                state.canvas.configure({
                    device, format: 'rgba8unorm', alphaMode: 'opaque',
                    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST
                });
                const info = adapter.info;
                result.adapter = {
                    vendor: info.vendor, architecture: info.architecture, device: info.device,
                    description: info.description, isFallbackAdapter: info.isFallbackAdapter ?? null
                };
                result.features = [...adapter.features].sort();
                result.limits = { maxTextureDimension2D: device.limits.maxTextureDimension2D };
                return state;
            } catch (error) {
                state.disposed = true;
                device.destroy();
                throw error;
            }
        },

        async renderAndRead(state, width, height, checkInvalidMap) {
            if (state.disposed) throw new Error('Context is disposed.');
            const { device } = state;
            const bytesPerRow = Math.ceil(width * 4 / 256) * 256;
            const texture = device.createTexture({
                size: [width, height], format: 'rgba8unorm',
                usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC
            });
            const buffer = device.createBuffer({
                size: bytesPerRow * height, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ
            });
            state.liveResources += 2;
            try {
                const encoder = device.createCommandEncoder();
                const pass = encoder.beginRenderPass({ colorAttachments: [{
                    view: texture.createView(), clearValue: [0, 0, 1, 1], loadOp: 'clear', storeOp: 'store'
                }] });
                pass.setPipeline(state.pipeline);
                pass.draw(3);
                pass.end();
                encoder.copyTextureToBuffer({ texture }, { buffer, bytesPerRow, rowsPerImage: height }, [width, height]);
                encoder.copyTextureToTexture({ texture }, { texture: state.canvas.getCurrentTexture() }, [width, height]);
                device.queue.submit([encoder.finish()]);
                if (checkInvalidMap) {
                    // A real WebGPU error, captured rather than left as a console error.
                    device.pushErrorScope('validation');
                    let rejected = false;
                    try { await buffer.mapAsync(GPUMapMode.READ, 1); }
                    catch { rejected = true; }
                    const validation = await device.popErrorScope();
                    if (!rejected || !validation) throw new Error('Invalid map alignment was not rejected.');
                    result.invalidMapRejected = true;
                }
                await buffer.mapAsync(GPUMapMode.READ);
                const padded = new Uint8Array(buffer.getMappedRange());
                const pixels = new Uint8Array(width * height * 4);
                for (let row = 0; row < height; row++)
                    pixels.set(padded.subarray(row * bytesPerRow, row * bytesPerRow + width * 4), row * width * 4);
                if (state.errors.length) throw new Error(state.errors.join('\n'));
                result.readback = { width, height, bytesPerRow, packedBytes: pixels.length };
                // Explicit copy for this proof. Production transport must measure a
                // byte/memory-view path before using per-element array marshalling.
                return pixels;
            } finally {
                if (buffer.mapState === 'mapped') buffer.unmap();
                buffer.destroy();
                texture.destroy();
                state.liveResources -= 2;
                result.liveResources = state.liveResources;
            }
        },

        copyReadback(pixels) { return Array.from(pixels); },
        async rejectReadback() { throw new Error('Injected asynchronous readback failure'); },
        reportFrame(iteration, foreground, background, elapsedMilliseconds) {
            result.frames.push({ iteration, foreground, background, elapsedMilliseconds });
        },
        reportManagedSuccess() { result.managedPixelsVerified = true; },
        disposeContext(state) {
            if (state.liveResources !== 0) throw new Error('Readback resources leaked.');
            state.disposed = true;
            state.canvas.unconfigure();
            state.device.destroy();
            result.disposed = true;
        }
    };
}

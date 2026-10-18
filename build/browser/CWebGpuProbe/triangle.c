// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#include <webgpu/webgpu.h>
#include <stdint.h>
#include <stdlib.h>

typedef void (*Completion)(int status, const uint8_t* pixels, int length, int stride, int invalid_map);
typedef struct Frame {
    WGPUInstance instance;
    WGPUAdapter adapter;
    WGPUDevice device;
    WGPUSurface surface;
    WGPUTexture texture;
    WGPUTexture canvas_texture;
    WGPUTextureView view;
    WGPUBuffer buffer;
    WGPUQueue queue;
    WGPUShaderModule shader;
    WGPURenderPipeline pipeline;
    int width, height, stride, check_invalid_map, invalid_map_rejected, mapped;
    Completion completed;
} Frame;

static int live_contexts;
static int last_adapter_type;

static WGPUStringView text(const char* value) {
    return (WGPUStringView){ value, WGPU_STRLEN };
}

// A frame owns all native references. Completion copies mapped bytes before
// returning; the managed continuation is scheduled after this cleanup.
static void finish(Frame* frame, int status) {
    const uint8_t* pixels = NULL;
    if (status == 0) {
        pixels = wgpuBufferGetConstMappedRange(frame->buffer, 0, frame->stride * frame->height);
        if (!pixels) status = -8;
    }
    frame->completed(status, pixels, frame->stride * frame->height, frame->stride, frame->invalid_map_rejected);
    if (frame->buffer) {
        if (frame->mapped) wgpuBufferUnmap(frame->buffer);
        wgpuBufferDestroy(frame->buffer);
        wgpuBufferRelease(frame->buffer);
    }
    if (frame->view) wgpuTextureViewRelease(frame->view);
    if (frame->canvas_texture) wgpuTextureRelease(frame->canvas_texture);
    if (frame->texture) { wgpuTextureDestroy(frame->texture); wgpuTextureRelease(frame->texture); }
    if (frame->pipeline) wgpuRenderPipelineRelease(frame->pipeline);
    if (frame->shader) wgpuShaderModuleRelease(frame->shader);
    if (frame->queue) wgpuQueueRelease(frame->queue);
    if (frame->surface) { if (frame->device) wgpuSurfaceUnconfigure(frame->surface); wgpuSurfaceRelease(frame->surface); }
    if (frame->device) { wgpuDeviceDestroy(frame->device); wgpuDeviceRelease(frame->device); }
    if (frame->adapter) wgpuAdapterRelease(frame->adapter);
    if (frame->instance) wgpuInstanceRelease(frame->instance);
    free(frame);
    live_contexts--;
}

static void mapped(WGPUMapAsyncStatus status, WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    frame->mapped = status == WGPUMapAsyncStatus_Success;
    finish(frame, frame->mapped ? 0 : -7);
}

static void map_valid(Frame* frame) {
    WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = mapped;
    callback.userdata1 = frame;
    wgpuBufferMapAsync(frame->buffer, WGPUMapMode_Read, 0, frame->stride * frame->height, callback);
}

static void invalid_scope_ready(WGPUPopErrorScopeStatus status, WGPUErrorType type,
                                WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    if (status != WGPUPopErrorScopeStatus_Success || type != WGPUErrorType_Validation || !frame->invalid_map_rejected) {
        finish(frame, -10);
        return;
    }
    map_valid(frame);
}

static void invalid_map_ready(WGPUMapAsyncStatus status, WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    frame->invalid_map_rejected = status != WGPUMapAsyncStatus_Success;
    frame->mapped = status == WGPUMapAsyncStatus_Success;
    WGPUPopErrorScopeCallbackInfo callback = WGPU_POP_ERROR_SCOPE_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = invalid_scope_ready;
    callback.userdata1 = frame;
    wgpuDevicePopErrorScope(frame->device, callback);
}

static void pipeline_ready(WGPUCreatePipelineAsyncStatus status, WGPURenderPipeline pipeline,
                           WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    frame->pipeline = pipeline;
    if (status != WGPUCreatePipelineAsyncStatus_Success || !pipeline) { finish(frame, -4); return; }

    WGPUSurfaceTexture current = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(frame->surface, &current);
    frame->canvas_texture = current.texture;
    if (!current.texture) { finish(frame, -5); return; }
    frame->view = wgpuTextureCreateView(frame->texture, NULL);
    WGPURenderPassColorAttachment color = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
    color.view = frame->view;
    color.clearValue = (WGPUColor){0, 0, 1, 1};
    color.loadOp = WGPULoadOp_Clear;
    color.storeOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor pass_descriptor = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
    pass_descriptor.colorAttachmentCount = 1;
    pass_descriptor.colorAttachments = &color;
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(frame->device, NULL);
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(encoder, &pass_descriptor);
    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

    WGPUTexelCopyTextureInfo source = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    source.texture = frame->texture;
    WGPUTexelCopyBufferInfo destination = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    destination.buffer = frame->buffer;
    destination.layout.bytesPerRow = frame->stride;
    destination.layout.rowsPerImage = frame->height;
    WGPUExtent3D extent = { frame->width, frame->height, 1 };
    wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
    WGPUTexelCopyTextureInfo canvas = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    canvas.texture = current.texture;
    wgpuCommandEncoderCopyTextureToTexture(encoder, &source, &canvas, &extent);
    WGPUCommandBuffer commands = wgpuCommandEncoderFinish(encoder, NULL);
    wgpuQueueSubmit(frame->queue, 1, &commands);
    wgpuCommandBufferRelease(commands);
    wgpuCommandEncoderRelease(encoder);
    // Browser presentation happens through the browser frame lifecycle.
    // Emdawn's wgpuSurfacePresent is unsupported and must not be called.
    if (frame->check_invalid_map) {
        wgpuDevicePushErrorScope(frame->device, WGPUErrorFilter_Validation);
        WGPUBufferMapCallbackInfo callback = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
        callback.mode = WGPUCallbackMode_AllowSpontaneous;
        callback.callback = invalid_map_ready;
        callback.userdata1 = frame;
        wgpuBufferMapAsync(frame->buffer, WGPUMapMode_Read, 1, 4, callback);
    } else {
        map_valid(frame);
    }
}

static void device_ready(WGPURequestDeviceStatus status, WGPUDevice device,
                         WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    frame->device = device;
    if (status != WGPURequestDeviceStatus_Success || !device) { finish(frame, -3); return; }
    frame->queue = wgpuDeviceGetQueue(device);

    WGPUSurfaceConfiguration config = WGPU_SURFACE_CONFIGURATION_INIT;
    config.device = device;
    config.format = WGPUTextureFormat_RGBA8Unorm;
    config.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopyDst;
    config.width = frame->width;
    config.height = frame->height;
    config.alphaMode = WGPUCompositeAlphaMode_Opaque;
    wgpuSurfaceConfigure(frame->surface, &config);

    WGPUTextureDescriptor texture = WGPU_TEXTURE_DESCRIPTOR_INIT;
    texture.size = (WGPUExtent3D){ frame->width, frame->height, 1 };
    texture.dimension = WGPUTextureDimension_2D;
    texture.format = WGPUTextureFormat_RGBA8Unorm;
    texture.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
    frame->texture = wgpuDeviceCreateTexture(device, &texture);
    WGPUBufferDescriptor buffer = WGPU_BUFFER_DESCRIPTOR_INIT;
    buffer.size = frame->stride * frame->height;
    buffer.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
    frame->buffer = wgpuDeviceCreateBuffer(device, &buffer);

    WGPUShaderSourceWGSL code = WGPU_SHADER_SOURCE_WGSL_INIT;
    code.code = text(
        "@vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {"
        "let p = array<vec2f,3>(vec2f(-0.8,-0.8),vec2f(0.8,-0.8),vec2f(0.0,0.8));"
        "return vec4f(p[index],0.0,1.0); }"
        "@fragment fn fs() -> @location(0) vec4f { return vec4f(1.0,0.0,0.0,1.0); }");
    WGPUShaderModuleDescriptor shader = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
    shader.nextInChain = &code.chain;
    frame->shader = wgpuDeviceCreateShaderModule(device, &shader);
    WGPUColorTargetState target = WGPU_COLOR_TARGET_STATE_INIT;
    target.format = WGPUTextureFormat_RGBA8Unorm;
    WGPUFragmentState fragment = WGPU_FRAGMENT_STATE_INIT;
    fragment.module = frame->shader;
    fragment.entryPoint = text("fs");
    fragment.targetCount = 1;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
    descriptor.vertex.module = frame->shader;
    descriptor.vertex.entryPoint = text("vs");
    descriptor.fragment = &fragment;
    WGPUCreateRenderPipelineAsyncCallbackInfo callback = WGPU_CREATE_RENDER_PIPELINE_ASYNC_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = pipeline_ready;
    callback.userdata1 = frame;
    wgpuDeviceCreateRenderPipelineAsync(device, &descriptor, callback);
}

static void adapter_ready(WGPURequestAdapterStatus status, WGPUAdapter adapter,
                          WGPUStringView message, void* userdata1, void* userdata2) {
    Frame* frame = userdata1;
    frame->adapter = adapter;
    if (status != WGPURequestAdapterStatus_Success || !adapter) { finish(frame, -2); return; }
    WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
    if (wgpuAdapterGetInfo(adapter, &info) == WGPUStatus_Success) {
        last_adapter_type = info.adapterType;
        wgpuAdapterInfoFreeMembers(info);
    }
    WGPUDeviceDescriptor descriptor = WGPU_DEVICE_DESCRIPTOR_INIT;
    WGPURequestDeviceCallbackInfo callback = WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = device_ready;
    callback.userdata1 = frame;
    wgpuAdapterRequestDevice(adapter, &descriptor, callback);
}

void triangle_render(int width, int height, int check_invalid_map, int no_adapter, Completion completed) {
    if (no_adapter) { completed(-101, NULL, 0, 0, 0); return; }
    if (width != 65 || height != 63) { completed(-100, NULL, 0, 0, 0); return; }
    Frame* frame = calloc(1, sizeof(Frame));
    if (!frame) { completed(-99, NULL, 0, 0, 0); return; }
    live_contexts++;
    frame->width = width;
    frame->height = height;
    frame->stride = ((width * 4 + 255) / 256) * 256;
    frame->check_invalid_map = check_invalid_map;
    frame->completed = completed;
    frame->instance = wgpuCreateInstance(NULL);
    if (!frame->instance) { finish(frame, -1); return; }
    WGPUEmscriptenSurfaceSourceCanvasHTMLSelector canvas = WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
    canvas.selector = text("#canvas");
    WGPUSurfaceDescriptor surface = WGPU_SURFACE_DESCRIPTOR_INIT;
    surface.nextInChain = &canvas.chain;
    frame->surface = wgpuInstanceCreateSurface(frame->instance, &surface);
    if (!frame->surface) { finish(frame, -6); return; }
    WGPURequestAdapterOptions options = WGPU_REQUEST_ADAPTER_OPTIONS_INIT;
    options.compatibleSurface = frame->surface;
    options.powerPreference = WGPUPowerPreference_HighPerformance;
    WGPURequestAdapterCallbackInfo callback = WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
    callback.mode = WGPUCallbackMode_AllowSpontaneous;
    callback.callback = adapter_ready;
    callback.userdata1 = frame;
    wgpuInstanceRequestAdapter(frame->instance, &options, callback);
}

int triangle_live_contexts(void) { return live_contexts; }
int triangle_adapter_type(void) { return last_adapter_type; }

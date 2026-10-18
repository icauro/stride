// Canvas DOM adapter. Input state and device processing remain in Stride C#.
export function attachBrowserInput(canvas, sink) {
    const lifetime = new AbortController();
    const signal = lifetime.signal;
    const oldTabIndex = canvas.getAttribute('tabindex');
    const oldTouchAction = canvas.style.touchAction;
    canvas.tabIndex = 0;
    canvas.style.touchAction = 'none';
    const active = new Map();
    const position = event => {
        const rect = canvas.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0
            ? [(event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height] : null;
    };
    const pause = () => { active.clear(); sink.pause(); };
    const pointer = (event, type) => {
        const point = position(event);
        if (!point) return;
        sink.surface(canvas.width, canvas.height);
        if (type === 0) {
            canvas.focus({ preventScroll: true });
            active.set(event.pointerId, event.pointerType);
            try { canvas.setPointerCapture(event.pointerId); }
            catch (error) { if (error.name !== 'NotFoundError') throw error; }
        }
        if (event.pointerType === 'mouse') {
            sink.move(...point);
            if (type === 0 || type === 2) sink.button(event.button, type === 0);
            else if (type === 3) sink.cancelMouse();
        } else sink.pointer(event.pointerId, type, ...point);
        if (type === 2 || type === 3) active.delete(event.pointerId);
        event.preventDefault();
    };
    canvas.addEventListener('keydown', event => { if (sink.key(event.code, true)) event.preventDefault(); }, { signal });
    canvas.addEventListener('keyup', event => { if (sink.key(event.code, false)) event.preventDefault(); }, { signal });
    for (const [event, type] of [['pointerdown', 0], ['pointermove', 1], ['pointerup', 2], ['pointercancel', 3]])
        canvas.addEventListener(event, value => pointer(value, type), { signal });
    canvas.addEventListener('lostpointercapture', event => {
        if (!active.has(event.pointerId)) return;
        if (active.get(event.pointerId) === 'mouse') sink.cancelMouse();
        else sink.pointer(event.pointerId, 3, 0, 0);
        active.delete(event.pointerId);
    }, { signal });
    canvas.addEventListener('wheel', event => {
        sink.wheel(-event.deltaY / (event.deltaMode === 0 ? 100 : event.deltaMode === 1 ? 3 : 1));
        event.preventDefault();
    }, { passive: false, signal });
    canvas.addEventListener('contextmenu', event => event.preventDefault(), { signal });
    canvas.addEventListener('blur', pause, { signal });
    window.addEventListener('blur', pause, { signal });
    document.addEventListener('visibilitychange', () => { if (document.hidden) pause(); }, { signal });
    sink.surface(canvas.width, canvas.height);
    return () => {
        lifetime.abort();
        for (const id of active.keys()) if (canvas.hasPointerCapture(id)) canvas.releasePointerCapture(id);
        pause();
        canvas.style.touchAction = oldTouchAction;
        if (oldTabIndex === null) canvas.removeAttribute('tabindex'); else canvas.setAttribute('tabindex', oldTabIndex);
    };
}

export function start(root, endpoint, pageId, labels) {
const stopped = new AbortController();
let socket, observer, resizeTimer, pointerAnimation, pointerVisibility;
let pointerRevision = null;
let pending = Promise.resolve();
let metadata = { width: 1000, height: 650 };
let nextFrame, painting = false;
let frames = 0, bytes = 0, lastInput = 0;
let pendingMove, moving = false;

// Input goes in order. The stream contains images from the actual Chromium page.
function command(value) {
    pending = pending.then(async () => {
        const response = await fetch(endpoint + '/command', {
            method: 'POST', headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': document.querySelector('#browser-request-verification input').value
            }, body: JSON.stringify({ ...value, pageId }), signal: stopped.signal
        });
        if (!response.ok) {
            const problem = response.headers.get('Content-Type')?.includes('json') ? await response.json() : null;
            throw new Error(problem?.detail || labels.failed);
        }
        const result = await response.json();
        if (result.cursor) { root.querySelector('canvas').style.cursor = result.cursor; }
        if (!['move', 'leave'].includes(value.kind)) { root.querySelector('[data-error]').textContent = ''; }
        return result;
    }).catch(error => { root.querySelector('[data-error]').textContent = error.message; return null; });
    return pending;
}

// Coalesce motion while a request is in flight so a drag cannot queue seconds of stale input.
function move() {
    if (moving || !pendingMove) { return; }
    moving = true;
    const value = pendingMove;
    pendingMove = null;
    command(value).finally(() => { moving = false; move(); });
}

function flushMove() {
    if (pendingMove) { command(pendingMove); pendingMove = null; }
}

// Keyboard shortcuts and the viewer's editing menu use the same clipboard bridge.
async function edit(action) {
    flushMove();
    if (action === 'all') { return command({ kind: 'key', text: 'Control+A' }); }
    try {
        if (!navigator.clipboard) { throw new Error(labels.clipboard); }
        if (action === 'paste') {
            const text = await navigator.clipboard.readText();
            await command({ kind: 'text', text });
        } else {
            const selection = await command({ kind: 'selection' });
            if (!selection?.text) { return; }
            await navigator.clipboard.writeText(selection.text);
            if (action === 'cut') { await command({ kind: 'cut', text: selection.text }); }
        }
    } catch (error) {
        root.querySelector('[data-error]').textContent = labels.clipboard + ' ' + error.message;
    }
}

function connect() {
    const canvas = root.querySelector('canvas');
    const panel = root.querySelector('[data-panel]');
    const context = canvas.getContext('2d');
    const pointer = root.querySelector('[data-agent-pointer]');
    socket = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + endpoint + '/' + encodeURIComponent(pageId) + '/frames');
    socket.onopen = () => { root.dataset.connected = 'true'; };
    socket.onclose = () => { if (!stopped.signal.aborted) { root.querySelector('[data-error]').textContent = labels.disconnected; } };
    socket.onmessage = event => {
        if (typeof event.data === 'string') { metadata = JSON.parse(event.data); return; }
        nextFrame = { data: event.data, dimensions: { ...metadata } };
        if (!painting) { paint(); }
    };
    // Decode one image at a time and retain only the latest waiting frame on a slow device.
    async function paint() {
        painting = true;
        try {
        while (nextFrame && !stopped.signal.aborted) {
            const { data, dimensions } = nextFrame;
            nextFrame = null;
            const bitmap = await createImageBitmap(data);
            if (stopped.signal.aborted) { bitmap.close(); break; }
            canvas.width = bitmap.width;
            canvas.height = bitmap.height;
            context.drawImage(bitmap, 0, 0);
            bitmap.close();
            // The overlay stays sharp at any stream resolution and never intercepts page input.
            if (dimensions.pointerX != null && dimensions.pointerY != null) {
                pointer.style.display = 'block';
                pointer.style.left = (dimensions.pointerX * canvas.clientWidth / dimensions.width - 2) + 'px';
                pointer.style.top = (dimensions.pointerY * canvas.clientHeight / dimensions.height - 2) + 'px';
                // Only a new action renews visibility, ordinary video frames must not keep it alive.
                if (pointerRevision !== dimensions.pointerRevision) {
                    pointerVisibility?.cancel();
                    pointerVisibility = pointer.animate([{ opacity: 1 }, { opacity: 0 }], {
                        delay: 3000, duration: matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 200, fill: 'both'
                    });
                }
                if (pointerRevision !== null && pointerRevision !== dimensions.pointerRevision
                    && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
                    pointerAnimation?.cancel();
                    pointerAnimation = pointer.animate([
                        { transform: 'rotate(0deg) scale(1)' },
                        { transform: 'rotate(-12deg) scale(.9)', offset: .3 },
                        { transform: 'rotate(7deg) scale(1.06)', offset: .65 },
                        { transform: 'rotate(0deg) scale(1)' }
                    ], { duration: 320, easing: 'ease-out' });
                }
            } else {
                pointer.style.display = 'none';
                pointerAnimation?.cancel();
                pointerVisibility?.cancel();
            }
            pointerRevision = dimensions.pointerRevision;
            canvas.dataset.viewportWidth = dimensions.width;
            canvas.dataset.viewportHeight = dimensions.height;
            canvas.dataset.frames = ++frames;
            bytes += data.size;
            canvas.dataset.bytes = bytes;
            if (lastInput) { canvas.dataset.inputToFrameMs = Math.round(performance.now() - lastInput); lastInput = 0; }

        }
        } catch (error) {
            if (!stopped.signal.aborted) { root.querySelector('[data-error]').textContent = labels.failed; }
        } finally { painting = false; }
    }
    if (matchMedia('(pointer: coarse)').matches) { return; }
    let pressed = false, clickCount = 0, lastDown = 0, lastPoint = { x: -100, y: -100 };
    function point(event) {
        const rect = canvas.getBoundingClientRect();
        const width = Number(canvas.dataset.viewportWidth), height = Number(canvas.dataset.viewportHeight);
        return { x: Math.max(0, Math.min(width - 1, (event.clientX - rect.left) * width / rect.width)),
            y: Math.max(0, Math.min(height - 1, (event.clientY - rect.top) * height / rect.height)) };
    }
    canvas.addEventListener('pointermove', event => { pendingMove = { kind: 'move', ...point(event) }; move(); });
    canvas.addEventListener('pointerleave', () => {
        if (pressed) { return; }
        flushMove();
        command({ kind: 'leave' });
    });
    canvas.addEventListener('pointerdown', event => {
        if (event.button !== 0) { return; }
        event.preventDefault();
        // Focusing a tall panel must not scroll the viewer and move the click's coordinate origin.
        canvas.focus({ preventScroll: true });
        canvas.setPointerCapture(event.pointerId);
        pressed = true;
        const position = point(event), now = performance.now();
        clickCount = now - lastDown < 500 && Math.hypot(position.x - lastPoint.x, position.y - lastPoint.y) < 5 ? clickCount % 3 + 1 : 1;
        lastDown = now;
        lastPoint = position;
        lastInput = performance.now();
        flushMove();
        command({ kind: 'down', ...position, clickCount });
    });
    function release(event) {
        if (!pressed) { return; }
        pressed = false;
        flushMove();
        const position = { x: event.clientX, y: event.clientY };
        command({ kind: 'up', ...point(event), clickCount }).then(result => {
            if (!result?.select || stopped.signal.aborted) { return; }
            const picker = root.querySelector('[data-option-picker]');
            picker.replaceChildren(...result.select.options.map((item, index) => {
                const option = new Option(item.label, String(index), false, item.selected);
                option.disabled = item.disabled;
                return option;
            }));
            picker.setAttribute('aria-label', result.select.label || optionLabel);
            picker.size = Math.max(2, Math.min(10, picker.options.length));
            picker.showPopover();
            picker.style.left = Math.max(0, Math.min(position.x, innerWidth - picker.offsetWidth)) + 'px';
            picker.style.top = Math.max(0, Math.min(position.y, innerHeight - picker.offsetHeight)) + 'px';
            picker.focus({ preventScroll: true });
        });
        if (canvas.hasPointerCapture(event.pointerId)) { canvas.releasePointerCapture(event.pointerId); }
    }
    canvas.addEventListener('pointerup', release);
    canvas.addEventListener('pointercancel', release);
    canvas.addEventListener('lostpointercapture', release);
    const optionPicker = root.querySelector('[data-option-picker]');
    const optionLabel = optionPicker.getAttribute('aria-label');
    optionPicker.addEventListener('change', () => {
        command({ kind: 'choose', text: optionPicker.value });
        optionPicker.hidePopover();
        canvas.focus({ preventScroll: true });
    });
    const editMenu = root.querySelector('[data-menu]');
    canvas.addEventListener('contextmenu', event => {
        event.preventDefault();
        canvas.focus({ preventScroll: true });
        editMenu.showPopover();
        editMenu.style.left = Math.max(0, Math.min(event.clientX, innerWidth - editMenu.offsetWidth)) + 'px';
        editMenu.style.top = Math.max(0, Math.min(event.clientY, innerHeight - editMenu.offsetHeight)) + 'px';
        editMenu.querySelector('button').focus({ preventScroll: true });
    });
    editMenu.querySelectorAll('button').forEach(button => {
        button.onclick = () => {
            editMenu.hidePopover();
            canvas.focus({ preventScroll: true });
            edit(button.dataset.edit);
        };
    });
    canvas.addEventListener('keydown', event => {
        if (event.isComposing) { return; }
        let text = event.key;
        const keys = ['Backspace', 'Delete', 'Enter', 'Tab', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'];
        const control = event.ctrlKey || event.metaKey;
        // Native paste events can supply text even where the Clipboard API is unavailable.
        if (control && !event.altKey && text.toLowerCase() === 'v') { return; }
        if (control && !event.altKey && ['c', 'x'].includes(text.toLowerCase())) {
            event.preventDefault();
            edit({ c: 'copy', x: 'cut' }[text.toLowerCase()]);
            return;
        }
        if (event.altKey || (control && !['a', 'z', 'y', 'arrowleft', 'arrowright', 'backspace', 'delete', 'home', 'end'].includes(text.toLowerCase()))) { return; }
        if (control && text.length === 1) { text = 'Control+' + (event.shiftKey && text.toLowerCase() === 'z' ? 'Y' : text.toUpperCase()); }
        else if (control) { text = 'Control+' + (event.shiftKey ? 'Shift+' : '') + text; }
        else if (event.shiftKey && ['Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(text)) { text = 'Shift+' + text; }
        else if (text.length !== 1 && !keys.includes(text)) { return; }
        event.preventDefault();
        lastInput = performance.now();
        flushMove();
        command({ kind: text.length === 1 ? 'text' : 'key', text });
    });
    canvas.addEventListener('paste', event => {
        event.preventDefault();
        command({ kind: 'text', text: event.clipboardData.getData('text/plain') });
    });
    canvas.addEventListener('wheel', event => {
        event.preventDefault();
        flushMove();
        command({ kind: 'scroll', x: event.deltaX, y: event.deltaY });
    }, { passive: false });
    observer = new ResizeObserver(() => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => command({ kind: 'resize', x: panel.clientWidth, y: panel.clientHeight }), 180);
    });
    observer.observe(panel);
}

// A recent viewer click can open the device picker. Otherwise the overlay remains a clickable fallback.
function showFilePicker() {
    const input = root.querySelector('[data-file-request] input[type=file]');
    if (!input || input.dataset.pickerBound) { return; }
    input.dataset.pickerBound = 'true';
    const request = input.closest('[data-file-request]');
    input.addEventListener('cancel', () => command({ kind: 'cancel-upload' }), { signal: stopped.signal });
    input.addEventListener('change', () => { request.hidden = false; }, { signal: stopped.signal });
    try { input.showPicker(); request.hidden = true; }
    catch (error) {
        if (error.name !== 'NotAllowedError') { root.querySelector('[data-error]').textContent = labels.failed; }
    }
}

connect(); return { showFilePicker, clearError() { root.querySelector('[data-error]').textContent = ''; }, dispose() { observer?.disconnect(); clearTimeout(resizeTimer); pointerAnimation?.cancel(); pointerVisibility?.cancel(); stopped.abort(); socket?.close(); } };

}

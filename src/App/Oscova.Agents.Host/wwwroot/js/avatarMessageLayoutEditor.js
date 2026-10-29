const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));

export function updateRect(start, handle, dx, dy, minimum = 0.01) {
    const right = start.left + start.width;
    const bottom = start.top + start.height;

    if (handle === "move") {
        return {
            left: clamp(start.left + dx, 0, 1 - start.width),
            top: clamp(start.top + dy, 0, 1 - start.height),
            width: start.width,
            height: start.height,
        };
    }

    let left = start.left;
    let top = start.top;
    let nextRight = right;
    let nextBottom = bottom;

    if (handle.includes("w")) left = clamp(start.left + dx, 0, right - minimum);
    if (handle.includes("e")) nextRight = clamp(right + dx, start.left + minimum, 1);
    if (handle.includes("n")) top = clamp(start.top + dy, 0, bottom - minimum);
    if (handle.includes("s")) nextBottom = clamp(bottom + dy, start.top + minimum, 1);

    return {
        left,
        top,
        width: nextRight - left,
        height: nextBottom - top,
    };
}

export function attachMessageLayoutEditor(screen, box, dotnet) {
    let gesture = null;
    const readRect = () => ({
        left: parseFloat(box.style.left) / 100,
        top: parseFloat(box.style.top) / 100,
        width: parseFloat(box.style.width) / 100,
        height: parseFloat(box.style.height) / 100,
    });
    const render = rectangle => {
        box.style.left = `${rectangle.left * 100}%`;
        box.style.top = `${rectangle.top * 100}%`;
        box.style.width = `${rectangle.width * 100}%`;
        box.style.height = `${rectangle.height * 100}%`;
    };
    const onPointerDown = event => {
        if (event.pointerType === "mouse" && event.button !== 0) return;
        const start = readRect();
        gesture = {
            pointerId: event.pointerId,
            x: event.clientX,
            y: event.clientY,
            start,
            current: start,
            handle: event.target.closest?.("[data-layout-handle]")?.dataset.layoutHandle ?? "move",
        };
        box.setPointerCapture(event.pointerId);
        event.preventDefault();
    };
    const onPointerMove = event => {
        if (!gesture || gesture.pointerId !== event.pointerId) return;
        const bounds = screen.getBoundingClientRect();
        gesture.current = updateRect(
            gesture.start,
            gesture.handle,
            (event.clientX - gesture.x) / bounds.width,
            (event.clientY - gesture.y) / bounds.height);
        render(gesture.current);
        event.preventDefault();
    };
    const finish = (commit, event) => {
        if (!gesture || (event && gesture.pointerId !== event.pointerId)) return;
        const completed = gesture;
        gesture = null;
        if (commit) {
            void dotnet.invokeMethodAsync(
                "OnLayoutChanged",
                completed.current.left,
                completed.current.top,
                completed.current.width,
                completed.current.height);
        } else {
            render(completed.start);
        }
        if (box.hasPointerCapture(completed.pointerId)) box.releasePointerCapture(completed.pointerId);
    };
    const onPointerUp = event => finish(true, event);
    const onPointerCancel = event => finish(false, event);
    const onLostPointerCapture = event => finish(true, event);

    box.addEventListener("pointerdown", onPointerDown);
    screen.addEventListener("pointermove", onPointerMove);
    screen.addEventListener("pointerup", onPointerUp);
    screen.addEventListener("pointercancel", onPointerCancel);
    box.addEventListener("lostpointercapture", onLostPointerCapture);

    return {
        dispose() {
            box.removeEventListener("pointerdown", onPointerDown);
            screen.removeEventListener("pointermove", onPointerMove);
            screen.removeEventListener("pointerup", onPointerUp);
            screen.removeEventListener("pointercancel", onPointerCancel);
            box.removeEventListener("lostpointercapture", onLostPointerCapture);
        },
    };
}

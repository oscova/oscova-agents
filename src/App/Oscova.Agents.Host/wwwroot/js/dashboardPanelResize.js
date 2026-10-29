import { getCategoryState as getStoredState, setCategoryState as setStoredState } from "./sidebarState.js";

const STORAGE_KEY = "oscova.dashboard.panelWidths";
const PANELS = {
    tree: { property: "--workspace-tree-width", minPixels: 160, maxPercent: 60, direction: -1 },
    thread: { property: "--dashboard-thread-width", minPixels: 240, maxPercent: 30, direction: 1 },
    details: { property: "--dashboard-details-width", minPixels: 380, maxPercent: 40, direction: -1 },
};

export function normalizeStoredWidth(value, maxPercent) {
    return Number.isFinite(value) ? Math.min(maxPercent, Math.max(0, value)) : null;
}

export function focusWorkspaceToggle(root) {
    const toggle = root.querySelector(".custom-chat-desktop-actions [data-workspace-toggle]");
    if (toggle?.getClientRects().length) { toggle.focus({ preventScroll: true }); }
}

export function attachWorkspaceTreeResize(root) {
    if (!root) return;
    for (const content of root.querySelectorAll(".custom-workspace-content")) {
        attachDashboardPanelResize(content, false);
    }
}

export function attachDashboardPanelResize(root, persist = true) {
    if (!root) return;
    const widths = persist ? getStoredState(STORAGE_KEY) ?? {} : {};
    for (const handle of root.querySelectorAll("[data-dashboard-resizer]")) {
        const kind = handle.dataset.dashboardResizer;
        if (kind === "tree" && persist) { continue; }
        if (!persist && handle.dataset.attached) { continue; }
        handle.dataset.attached = "true";
        const config = PANELS[kind];
        const panel = handle.parentElement;
        if (!config || !panel) continue;
        const currentPercent = () => panel.getBoundingClientRect().width / root.clientWidth * 100;
        // Layout direction can change while these handlers remain attached.
        const direction = () => config.direction * (getComputedStyle(root).direction === "rtl" ? -1 : 1);
        const updateAria = () => {
            handle.setAttribute("aria-valuemin", (config.minPixels / root.clientWidth * 100).toFixed(1));
            handle.setAttribute("aria-valuenow", currentPercent().toFixed(1));
        };
        const applyWidth = value => {
            root.style.setProperty(config.property, `${value}%`);
            widths[kind] = Math.round(currentPercent() * 100) / 100;
            updateAria();
        };
        widths[kind] = normalizeStoredWidth(widths[kind], config.maxPercent);
        if (widths[kind] === null) delete widths[kind];
        else root.style.setProperty(config.property, `${widths[kind]}%`);
        updateAria();

        let drag;
        handle.onpointerdown = event => {
            if (event.button !== 0) return;
            drag = { x: event.clientX, width: currentPercent(), pointerId: event.pointerId };
            root.classList.add("dashboard-resizing");
            handle.setPointerCapture(event.pointerId);
            event.preventDefault();
        };
        handle.onpointermove = event => {
            if (drag) { applyWidth(drag.width + (event.clientX - drag.x) / root.clientWidth * 100 * direction()); }
        };
        const finish = () => {
            if (!drag) return;
            const pointerId = drag.pointerId;
            drag = null;
            root.classList.remove("dashboard-resizing");
            if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
            if (persist) { setStoredState(STORAGE_KEY, widths); }
        };
        handle.onpointerup = handle.onpointercancel = handle.onlostpointercapture = finish;
        handle.onfocus = updateAria;
        handle.onkeydown = event => {
            if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
            applyWidth(currentPercent() + (event.key === "ArrowRight" ? 2 : -2) * direction());
            if (persist) { setStoredState(STORAGE_KEY, widths); }
            event.preventDefault();
        };
    }
}

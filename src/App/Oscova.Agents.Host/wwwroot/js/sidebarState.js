export { isDesktop } from "./utils.js";

export function getCategoryState(storageKey) {
    try {
        const raw = localStorage.getItem(storageKey);
        if (!raw) {
            return null;
        }

        const parsed = JSON.parse(raw);
        return parsed && typeof parsed === "object" && !Array.isArray(parsed)
            ? parsed
            : null;
    } catch {
        return null;
    }
}

export function setCategoryState(storageKey, state) {
    try {
        localStorage.setItem(storageKey, JSON.stringify(state ?? {}));
    } catch {
        // Storage can be unavailable in private browsing or locked-down browsers.
    }
}

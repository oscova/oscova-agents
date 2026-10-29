const allowedTimeouts = new Set([0, 5, 15, 30]);
const activityEvents = ["pointerdown", "pointermove", "keydown", "wheel"];
const activityThrottleMs = 1000;

let abortController;
let channel;
let timer;
let timeoutMinutes = 0;
let lastActivity = 0;
let lastDomActivity = 0;
let logoutRoute = "";
let userId = "";
let loggingOut = false;

export function initialize(minutes, route, currentUserId) {
    dispose();
    if (!currentUserId) {
        throw new Error("Automatic sign-out requires an authenticated user.");
    }

    timeoutMinutes = allowedTimeouts.has(minutes) ? minutes : 0;
    logoutRoute = route;
    userId = currentUserId;
    abortController = new AbortController();
    channel = new BroadcastChannel(`oscova-idle-logout:${userId}`);
    channel.addEventListener("message", receiveMessage);

    const listenerOptions = { capture: true, passive: true, signal: abortController.signal };
    for (const eventName of activityEvents) {
        window.addEventListener(eventName, recordDomActivity, listenerOptions);
    }
    document.addEventListener("visibilitychange", checkWhenVisible, { signal: abortController.signal });

    recordActivity(true);
}

export function setTimeoutMinutes(minutes) {
    if (!allowedTimeouts.has(minutes) || minutes === timeoutMinutes) {
        return;
    }

    applyTimeout(minutes);
    channel?.postMessage({ type: "timeout", minutes });
}

export function recordSubmit() {
    recordActivity(true);
}

function recordDomActivity() {
    recordActivity(false);
}

function recordActivity(force) {
    const now = Date.now();
    if (!force && now - lastDomActivity < activityThrottleMs) {
        return;
    }

    lastDomActivity = now;
    lastActivity = now;
    channel?.postMessage({ type: "activity", timestamp: now });
    scheduleExpiry();
}

function receiveMessage(event) {
    const message = event.data;
    if (!message || typeof message !== "object") {
        return;
    }

    if (message.type === "activity" && Number.isFinite(message.timestamp)) {
        lastActivity = Math.max(lastActivity, message.timestamp);
        scheduleExpiry();
        return;
    }

    if (message.type === "timeout" && allowedTimeouts.has(message.minutes)) {
        applyTimeout(message.minutes);
        return;
    }

    if (message.type === "expired") {
        beginLogout(false);
    }
}

function applyTimeout(minutes) {
    const wasDisabled = timeoutMinutes === 0;
    timeoutMinutes = minutes;
    if (wasDisabled && minutes > 0) {
        lastActivity = Date.now();
    }
    scheduleExpiry();
}

function checkWhenVisible() {
    if (document.visibilityState === "visible") {
        checkExpiry();
    }
}

function checkExpiry() {
    if (timeoutMinutes === 0 || loggingOut) {
        return;
    }

    if (Date.now() - lastActivity >= timeoutMinutes * 60_000) {
        beginLogout(true);
        return;
    }

    scheduleExpiry();
}

function scheduleExpiry() {
    clearTimeout(timer);
    timer = undefined;
    if (timeoutMinutes === 0 || loggingOut) {
        return;
    }

    const remaining = lastActivity + timeoutMinutes * 60_000 - Date.now();
    timer = window.setTimeout(checkExpiry, Math.max(0, remaining));
}

function beginLogout(announce) {
    if (loggingOut) {
        return;
    }

    loggingOut = true;
    clearTimeout(timer);
    timer = undefined;
    if (announce) {
        channel?.postMessage({ type: "expired" });
    }

    const target = new URL(logoutRoute, window.location.origin);
    target.searchParams.set("expectedUserId", userId);
    window.location.replace(target.toString());
}

export function dispose() {
    abortController?.abort();
    abortController = undefined;
    clearTimeout(timer);
    timer = undefined;
    channel?.close();
    channel = undefined;
    timeoutMinutes = 0;
    lastActivity = 0;
    lastDomActivity = 0;
    logoutRoute = "";
    userId = "";
    loggingOut = false;
}

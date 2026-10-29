// Utilities shared across Blazor components (ES module)
// - scroll helpers: preserve follow-latest behavior in scrollable timelines
// - isDesktop: returns true when viewport width >= 1024px
// - downloadFile: downloads a file from base64 data
// - downloadFileFromStream: downloads a streamed .NET response
// - postDownloadFile: submits a POST form so the browser can stream a download natively
// - postUploadFile: uploads a file with fields through a native browser multipart POST
// - restoreEnvironmentBackup: uploads a backup, waits for host return, then navigates after restart
// - resetEnvironment: submits an authorized reset, polls its opaque receipt, then navigates after restart
// - copyToClipboard: copies text to the clipboard
// - closeDropdowns/hidePopover: closes open DaisyUI dropdowns
// - showModal: opens a <dialog> element via ElementReference
// - closeModal: closes a <dialog> element via ElementReference
// - insertTextAtCursor: inserts text into a textarea by id at the current caret or selection
// - removeLeadingText: strips a leading token from a textarea by id

let chatComposerEnterGuardInitialized = false;

export function initializeChatComposerEnterGuard() {
    if (chatComposerEnterGuardInitialized) {
        return;
    }

    document.addEventListener("keydown", event => {
        const composer = event.target?.matches?.("[data-chat-composer]") ? event.target : null;
        if (!composer) {
            return;
        }

        const slashMenuOpen = composer.dataset.chatSlashMenuOpen === "true";
        if ((event.key === "Enter" && !event.shiftKey) ||
            (slashMenuOpen && ["ArrowUp", "ArrowDown", "Tab", "Escape"].includes(event.key))) {
            event.preventDefault();
        }
    }, true);
    chatComposerEnterGuardInitialized = true;
}

export function showModal(element) { element.showModal(); }
export function closeModal(element) { element.close(); }

export function closeDropdowns() {
    // Keep native popover focus restoration so a following dialog remembers its trigger.
    document.querySelectorAll(":popover-open").forEach(hidePopover);
}

export function hidePopover(element) {
    if (element?.matches(":popover-open")) {
        element.hidePopover();
    }
}

export function showPopover(element) {
    element.showPopover();
}

export function setCheckbox(elementId, checked) {
    const element = document.getElementById(elementId);
    if (element && "checked" in element) {
        element.checked = !!checked;
    }
}

// Inserts text into a textarea at the current selection/caret and returns the new value.
export function insertTextAtCursor(elementId, text) {
    if (!elementId) {
        return null;
    }

    const element = document.getElementById(elementId);
    if (!element || typeof element.value !== "string") {
        return null;
    }

    if (typeof element.focus !== "function" || typeof element.setSelectionRange !== "function") {
        return null;
    }

    const currentValue = element.value ?? "";
    const start = typeof element.selectionStart === "number" ? element.selectionStart : currentValue.length;
    const end = typeof element.selectionEnd === "number" ? element.selectionEnd : currentValue.length;

    const updatedValue = `${currentValue.slice(0, start)}${text}${currentValue.slice(end)}`;
    const caretPosition = start + text.length;

    element.value = updatedValue;
    element.focus();
    element.setSelectionRange(caretPosition, caretPosition);

    return updatedValue;
}

// Removes a leading token from the live textarea value so text typed after it survives server round trips.
export function removeLeadingText(elementId, prefix) {
    const element = document.getElementById(elementId);
    if (!element?.value.startsWith(prefix)) {
        return;
    }

    // No input event: the server strips its own copy, and an echoed value could restore text that was already sent.
    element.value = element.value.slice(prefix.length);
}

export function isScrolledNearBottom(element, threshold = 72) {
    if (!element) {
        return true;
    }

    return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
}

export function scrollToLatest(element, smooth = false) {
    if (!element) {
        return;
    }

    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    element.scrollTo({
        top: element.scrollHeight,
        behavior: smooth && !reduceMotion ? 'smooth' : 'auto'
    });
}

export function getChatScrollMetrics(element) {
    return element ? [element.scrollTop, element.scrollHeight] : [0, 0];
}

export function restoreChatScroll(element, top) {
    if (element) { element.scrollTop = top; }
}

export function restorePrependedChatScroll(element, top, previousHeight) {
    if (element) { element.scrollTop = top + element.scrollHeight - previousHeight; }
}

export function isDesktop() {
    try {
        if (window.matchMedia) {
            return window.matchMedia('(min-width: 1024px)').matches;
        }
        return window.innerWidth >= 1024;
    } catch (e) {
        return true; // default to desktop if JS env check fails
    }
}

export function downloadFile(fileName, base64Data) {
    const byteCharacters = atob(base64Data);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    downloadBlob(fileName, byteArray);
}

export async function downloadFileFromStream(fileName, streamReference) {
    downloadBlob(fileName, await streamReference.arrayBuffer());
}

function downloadBlob(fileName, data) {
    const extension = fileName.split('.').pop().toLowerCase();
    const mimeTypes = {
        'json': 'application/json',
        'png': 'image/png',
        'jpg': 'image/jpeg',
        'jpeg': 'image/jpeg',
        'gif': 'image/gif',
        'pdf': 'application/pdf',
        'txt': 'text/plain'
    };
    const mimeType = mimeTypes[extension] || 'application/octet-stream';
    const blob = new Blob([data], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
}

export function postDownloadFile(url, fields) {
    let iframe = document.getElementById('download-frame');
    if (!iframe) {
        iframe = document.createElement('iframe');
        iframe.id = 'download-frame';
        iframe.name = 'download-frame';
        iframe.style.display = 'none';
        document.body.appendChild(iframe);
    }

    const form = document.createElement('form');
    form.method = 'post';
    form.action = url;
    form.target = 'download-frame';
    form.style.display = 'none';

    // Native streaming downloads use the cookie session and the document's request token.
    const verificationInput = document.querySelector('#browser-request-verification input');
    form.appendChild(verificationInput.cloneNode(true));

    for (const [key, value] of Object.entries(fields ?? {})) {
        if (value === null || value === undefined || value === '') {
            continue;
        }

        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = key;
        input.value = value;
        form.appendChild(input);
    }

    document.body.appendChild(form);
    form.submit();
    form.remove();
}

export async function postUploadFile(url, fileInputId, fields) {
    const input = document.getElementById(fileInputId);
    const file = input?.files?.[0];
    if (!file) {
        return { ok: false, status: 0, body: "No file selected." };
    }

    const form = new FormData();
    form.append("file", file, file.name);
    for (const [key, value] of Object.entries(fields ?? {})) {
        if (value !== null && value !== undefined && value !== "") {
            form.append(key, value);
        }
    }

    const response = await fetch(url, { method: "POST", body: form });
    return { ok: response.ok, status: response.status, body: await response.text() };
}

export async function waitForHostReturn(url, attempts, delayMs) {
    for (let attempt = 0; attempt < attempts; attempt++) {
        try {
            const response = await fetch(url, { method: "GET", cache: "no-store" });
            if (response.ok || response.status === 302 || response.status === 401) {
                return true;
            }
        } catch {
        }

        await new Promise(resolve => setTimeout(resolve, delayMs));
    }

    return false;
}

let activeApplicationUpdate = false;
// The updater stops the Host within seconds. A Host still up after this long means the updater ended first.
const applicationUpdateStartLimitMs = 120000;

// Return immediately so the Blazor interop call does not own the update's lifetime.
// The update stops the Host, so the page reloads once the Host has gone away and answers again.
export function waitForApplicationUpdate(versionUrl, statusUrl, expectedVersion, returnUrl, statusElementId, waitingMessage, longerMessage, notStartedMessage) {
    if (activeApplicationUpdate) { return; }
    if (sessionStorage.getItem("oscova-update-version") !== expectedVersion || !sessionStorage.getItem("oscova-update-started")) {
        sessionStorage.setItem("oscova-update-started", String(Date.now()));
        sessionStorage.removeItem("oscova-update-down");
    }
    sessionStorage.setItem("oscova-update-version", expectedVersion);
    activeApplicationUpdate = true;
    setApplicationUpdateStatus(statusElementId, waitingMessage, false);
    void monitor();

    async function readJson(url) {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 5000);
        try {
            const response = await fetch(url, { method: "GET", cache: "no-store", credentials: "same-origin", signal: controller.signal });
            return response.ok ? await response.json() : { httpStatus: response.status };
        } catch {
            return null;
        } finally {
            clearTimeout(timeout);
        }
    }

    async function monitor() {
        try {
            while (true) {
                const started = Number(sessionStorage.getItem("oscova-update-started"));
                setApplicationUpdateStatus(statusElementId, Date.now() - started >= 300000 ? longerMessage : waitingMessage, false);
                const version = await readJson(versionUrl);
                if (!version || version.httpStatus) {
                    sessionStorage.setItem("oscova-update-down", "true");
                } else {
                    // A restart can finish between polls, so an ended session or a finished result also proves it.
                    // The version cannot: a plugin-only update restarts into the same version.
                    // The restarted Host opens the update result for the operator, whether it succeeded or failed.
                    const restarted = sessionStorage.getItem("oscova-update-down");
                    const status = restarted ? null : await readJson(statusUrl);
                    if (restarted || status?.httpStatus === 401 || status?.state === "Succeeded") {
                        finishApplicationUpdate(statusElementId);
                        window.location.assign(returnUrl);
                        return;
                    }
                    if (Date.now() - started >= applicationUpdateStartLimitMs) {
                        // The reload closes the waiting dialog, and the reloaded page shows why.
                        finishApplicationUpdate(statusElementId);
                        sessionStorage.setItem("oscova-update-not-started", notStartedMessage);
                        window.location.assign(returnUrl);
                        return;
                    }
                }
                await new Promise(resolve => setTimeout(resolve, 5000));
            }
        } finally {
            activeApplicationUpdate = false;
        }
    }
}

export function resumeApplicationUpdate(versionUrl, statusUrl, returnUrl, statusElementId, waitingMessage, longerMessage, notStartedMessage) {
    const notStarted = sessionStorage.getItem("oscova-update-not-started");
    if (notStarted) {
        sessionStorage.removeItem("oscova-update-not-started");
        setApplicationUpdateStatus(statusElementId, notStarted, true);
        return;
    }
    const expectedVersion = sessionStorage.getItem("oscova-update-version");
    if (expectedVersion) {
        waitForApplicationUpdate(versionUrl, statusUrl, expectedVersion, returnUrl, statusElementId, waitingMessage, longerMessage, notStartedMessage);
    }
}

function finishApplicationUpdate(statusElementId) {
    sessionStorage.removeItem("oscova-update-down");
    sessionStorage.removeItem("oscova-update-version");
    sessionStorage.removeItem("oscova-update-started");
    setApplicationUpdateStatus(statusElementId, null, false);
}

function setApplicationUpdateStatus(elementId, message, failed) {
    // The reconnect modal covers this toast while the server restarts, so it shows the same message.
    const reconnect = document.getElementById("components-reconnect-modal");
    if (reconnect) {
        if (message) { reconnect.dataset.updateMessage = message; } else { delete reconnect.dataset.updateMessage; }
        reconnect.dataset.updateFailed = String(failed);
        reconnect.dispatchEvent(new Event("oscova-update-message"));
    }
    const container = document.getElementById(elementId);
    const alert = container?.querySelector("[role=alert]");
    if (!container || !alert) { return; }
    alert.querySelector("span").textContent = message ?? "";
    // Only a failure stays up. The waiting message replaces itself on every poll, so it has nothing to dismiss.
    const close = alert.querySelector("button");
    close.hidden = !failed;
    close.onclick = () => { container.hidden = true; };
    alert.classList.toggle("alert-error", failed);
    alert.classList.toggle("alert-info", !failed);
    container.hidden = !message;
}
export async function restoreEnvironmentBackup(url, fileInputId, fields, returnUrl, attempts, delayMs) {
    const upload = await postUploadFile(url, fileInputId, fields);
    if (upload.status !== 202) {
        return upload;
    }

    const restored = await waitForHostReturn(returnUrl, attempts, delayMs);
    if (restored) {
        window.location.assign(returnUrl);
        return upload;
    }

    return {
        ok: false,
        status: 0,
        body: "Oscova did not restart before the wait timed out."
    };
}

export async function resetEnvironment(url, token, request, completionBaseUrl, returnUrl, statusId, messages) {
    let response;
    try {
        response = await fetch(url, {
            method: "POST",
            credentials: "omit",
            headers: {
                "Content-Type": "application/json",
                Authorization: `Bearer ${token}`
            },
            body: JSON.stringify(request)
        });
    } catch {
        setEnvironmentResetStatus(statusId, messages.uncertain);
        return { accepted: true, completed: false, uncertain: true };
    } finally {
        token = null;
    }

    if (response.status !== 202) {
        return { accepted: false, status: response.status, body: await response.text() };
    }

    let accepted;
    try {
        accepted = await response.json();
    } catch {
        setEnvironmentResetStatus(statusId, messages.uncertain);
        return { accepted: true, completed: false, uncertain: true };
    }

    if (!accepted?.operationId || !accepted?.backupPath) {
        setEnvironmentResetStatus(statusId, messages.uncertain);
        return { accepted: true, completed: false, uncertain: true };
    }

    setEnvironmentResetStatus(statusId, `${messages.backup} ${accepted.backupPath}. ${messages.restarting}`);
    const attempts = 300;
    const delayMs = 2000;
    const deadline = Date.now() + attempts * delayMs;
    const completionUrl = completionBaseUrl + encodeURIComponent(accepted.operationId);

    for (let attempt = 0; attempt < attempts && Date.now() < deadline; attempt++) {
        const controller = new AbortController();
        const requestTimeout = Math.min(delayMs, deadline - Date.now());
        const timeout = setTimeout(() => controller.abort(), requestTimeout);
        try {
            const completionResponse = await fetch(completionUrl, {
                method: "GET",
                credentials: "omit",
                cache: "no-store",
                signal: controller.signal
            });
            if (completionResponse.ok) {
                const completion = await completionResponse.json();
                if (completion.completed === true) {
                    window.location.assign(returnUrl);
                    return { accepted: true, completed: true };
                }
            }
        } catch {
        } finally {
            clearTimeout(timeout);
        }

        const remaining = deadline - Date.now();
        if (remaining > 0) {
            await new Promise(resolve => setTimeout(resolve, Math.min(delayMs, remaining)));
        }
    }

    setEnvironmentResetStatus(statusId, `${messages.backup} ${accepted.backupPath}. ${messages.timeout}`);
    return { accepted: true, completed: false };
}

function setEnvironmentResetStatus(elementId, message) {
    const element = document.getElementById(elementId);
    if (element) {
        element.textContent = message ?? "";
    }
}

export async function copyToClipboard(text) {
    // Modern clipboard API (requires secure context: https:// or localhost)
    if (window.isSecureContext && navigator.clipboard?.writeText) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (error) {
            console.error('Failed to copy to clipboard:', error);
            return false;
        }
    }
    
    // Fallback for insecure contexts (http://<LAN-IP>)
    try {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.left = '-9999px';
        textarea.style.top = '-9999px';
        document.body.appendChild(textarea);
        textarea.select();
        textarea.setSelectionRange(0, text.length);
        const success = document.execCommand('copy');
        document.body.removeChild(textarea);
        return success;
    } catch (error) {
        console.error('Failed to copy to clipboard (fallback):', error);
        return false;
    }
}
// Chat owns this routing. Modified clicks and other Markdown surfaces retain native hyperlink behavior.
export function initializeBrowserLinks(root, dotNet) {
    const stopped = new AbortController();
    // Claim chat links before document navigation, including dialogs outside the panel.
    document.addEventListener('click', event => {
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) { return; }
        const surface = event.target.closest('[data-browser-chat]');
        if (!surface || surface.dataset.browserChat !== root.dataset.browserChat) { return; }
        const link = event.target.closest('.markdown-content a[href]');
        if (!link || link.hasAttribute('download')) { return; }
        const artifactId = link.getAttribute('data-chat-artifact');
        if (artifactId) {
            event.preventDefault();
            dotNet.invokeMethodAsync('OpenArtifactLink', root.dataset.browserChat, artifactId);
            return;
        }
        if (root.dataset.browserEnabled !== 'true' || !/^https?:\/\//i.test(link.getAttribute('href'))) { return; }
        event.preventDefault();
        dotNet.invokeMethodAsync('OpenBrowserLink', root.dataset.browserChat, link.href);
    }, { capture: true, signal: stopped.signal });
    return { dispose() { stopped.abort(); } };
}

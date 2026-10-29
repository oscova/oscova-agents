// Minimal queued audio player for Blazor (ES module)
let queue = [];
let isPlaying = false;
let audioEl = null;
let pendingUnlock = false;
let dotnetRef = null;
let playbackGeneration = 0;

export function init(audioElementId) {
    audioEl = document.getElementById(audioElementId);
    if (!audioEl) {
        console.error("[audioQueue] Audio element not found:", audioElementId);
        return;
    }
    audioEl.onerror = (e) => {
        console.warn("[audioQueue] Audio error:", audioEl?.error?.code || e);
        isPlaying = false;
        if (queue.length > 0) {
            playNext();
        } else {
            notifyAllPlaybackFinished();
        }
    };
}

export function activate() {
    if (!audioEl || isPlaying) { return; }
    // Prime autoplay with a short silent clip
    audioEl.muted = true;
    audioEl.src = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAgD4AAAB9AAACABAAZGF0YQAAAAA=";
    audioEl.play().then(() => {
        audioEl.pause();
        audioEl.currentTime = 0;
        audioEl.muted = false;
        if (!isPlaying && queue.length) { playNext(); }
    }).catch(err => {
        audioEl.muted = false;
        console.warn("[audioQueue] activate() play() blocked:", err);
        requestUnlock();
    });
}

export function setDotNetRef(ref) {
    dotnetRef = ref;
}

export function enqueueBase64(base64, mime = "audio/wav") {
    try {
        const binary = atob(base64);
        const len = binary.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) { bytes[i] = binary.charCodeAt(i); }
        const blob = new Blob([bytes], { type: mime });
        const url = URL.createObjectURL(blob);
        queue.push(url);
        if (!isPlaying) { playNext(); }
    } catch (e) {
        console.error("[audioQueue] Failed to decode base64:", e);
    }
}

export function enqueueUrl(url) {
    if (!url) { return; }
    // push server-hosted URL (e.g., /tts/{user}/{file}.wav)
    queue.push(url);
    if (!isPlaying) { playNext(); }
}

export function clear() {
    // Pending play callbacks must not restore audio cleared by the user.
    playbackGeneration++;
    queue.forEach(url => { try { if (typeof url === "string" && url.startsWith("blob:")) { URL.revokeObjectURL(url); } } catch { } });
    queue = [];
    if (!audioEl) { return; }
    audioEl.pause();
    audioEl.currentTime = 0;
    audioEl.removeAttribute("src");
    audioEl.load();
    isPlaying = false;
}

export function resume() {
    if (!isPlaying && queue.length) { playNext(); }
}

function requestUnlock() {
    if (pendingUnlock) { return; }
    pendingUnlock = true;
    const handler = () => {
        pendingUnlock = false;
        window.removeEventListener("click", handler);
        audioEl.muted = false;
        resume();
    };
    window.addEventListener("click", handler, { once: true });
}

async function notifyPlaybackStarted(url) {
    if (dotnetRef && dotnetRef.invokeMethodAsync) {
        try {
            await dotnetRef.invokeMethodAsync('OnAudioPlaybackStarted', url);
        } catch (e) {
            console.warn("[audioQueue] Failed to notify .NET:", e);
        }
    }
}

async function notifyAllPlaybackFinished() {
    if (dotnetRef && dotnetRef.invokeMethodAsync) {
        try {
            await dotnetRef.invokeMethodAsync('OnAllPlaybackFinished');
        } catch (e) {
            console.warn("[audioQueue] Failed to notify .NET:", e);
        }
    }
}

function playNext() {
    if (!audioEl || queue.length === 0) { return; }
    const generation = playbackGeneration;
    isPlaying = true;
    const url = queue.shift();
    audioEl.src = url;
    audioEl.play().then(() => {
        if (generation !== playbackGeneration) { return; }
        notifyPlaybackStarted(url);
    }).catch(err => {
        if (generation !== playbackGeneration) { return; }
        console.warn("[audioQueue] play() blocked:", err);
        // retry later on user gesture
        queue.unshift(url);
        isPlaying = false;
        requestUnlock();
    });
    audioEl.onended = () => {
        if (generation !== playbackGeneration) { return; }
        try { if (typeof url === "string" && url.startsWith("blob:")) { URL.revokeObjectURL(url); } } catch { }
        isPlaying = false;
        if (queue.length > 0) {
            playNext();
        } else {
            notifyAllPlaybackFinished();
        }
    };
}

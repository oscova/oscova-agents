// Plays short, one-shot UI sound cues. Kept separate from audioQueue.js (TTS playback)
// because these are tiny, fire-and-forget feedback sounds with their own enable/volume gating.

const cues = {};        // id -> HTMLAudioElement template, cloned per play so rapid repeats can overlap
let enabled = true;
let volume = 0.35;
let cueEnabled = {};    // id -> bool; a missing id means the cue is enabled

export function configure(options) {
    if (!options) {
        return;
    }
    enabled = options.enabled !== false;
    if (typeof options.volume === "number") {
        volume = options.volume;
    }
    cueEnabled = options.cues || {};
}

export function preload(ids, basePath) {
    for (const id of ids) {
        const audio = new Audio(`${basePath}/${id}.wav`);
        audio.preload = "auto";
        cues[id] = audio;
    }
}

export function play(id) {
    if (!enabled || cueEnabled[id] === false) {
        return;
    }
    const template = cues[id];
    if (!template) {
        return; // missing cue file -> no-op
    }
    const node = template.cloneNode();
    node.volume = volume;
    // Autoplay policy can reject playback before the first user gesture; that is expected and harmless.
    const result = node.play();
    if (result && typeof result.catch === "function") {
        result.catch(() => {});
    }
}

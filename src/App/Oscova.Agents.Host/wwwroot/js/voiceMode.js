// Voice Mode — continuous voice conversation using @ricky0123/vad-web (ES module)
// VAD detects speech segments, encodes to WAV, and sends base64 to .NET for transcription.
// Usage from Blazor: const mod = await import('/js/voiceMode.js'); await mod.start(dotNetRef); mod.pause(); mod.resume(); await mod.stop();
import { encodeWav } from '/js/audioRecorder.js';

const VadAssetPath = '/lib/vad/';

let dotNetRef = null;
let micVad = null;
let vadLibraryLoaded = false;

// Dynamically loads the VAD bundle (ort.min.js + vad bundle.min.js) on first use.
// After loading, window.vad.MicVAD is available.
async function loadVadLibrary() {
    if (vadLibraryLoaded) return;

    await loadScript(VadAssetPath + 'ort.min.js');
    await loadScript(VadAssetPath + 'bundle.min.js');
    vadLibraryLoaded = true;
}

function loadScript(src) {
    return new Promise((resolve, reject) => {
        // Avoid duplicate script tags
        if (document.querySelector(`script[src="${src}"]`)) {
            resolve();
            return;
        }
        const script = document.createElement('script');
        script.src = src;
        script.onload = resolve;
        script.onerror = () => reject(new Error(`Failed to load ${src}`));
        document.head.appendChild(script);
    });
}

// Converts Float32Array audio samples to a base64-encoded WAV string.
function audioToBase64Wav(float32Audio, sampleRate) {
    const wavBuffer = encodeWav(float32Audio, sampleRate, 1);
    const bytes = new Uint8Array(wavBuffer);
    const chunkSize = 0x8000;
    const chunks = [];

    for (let i = 0; i < bytes.length; i += chunkSize) {
        const subarray = bytes.subarray(i, i + chunkSize);
        chunks.push(String.fromCharCode.apply(null, subarray));
    }

    const binary = chunks.join('');
    return btoa(binary);
}

// Creates and starts the MicVAD instance. Calls dotNetRef.OnVadSpeechEnd with base64 WAV on each utterance.
export async function start(ref) {
    if (micVad) return;
    dotNetRef = ref;

    await loadVadLibrary();

    micVad = await window.vad.MicVAD.new({
        baseAssetPath: VadAssetPath,
        onnxWASMBasePath: VadAssetPath,
        startOnLoad: true,


        onSpeechStart: () => {
            dotNetRef?.invokeMethodAsync('OnVadSpeechStart');
        },

        onSpeechEnd: (audio) => {
            // audio is Float32Array at 16kHz mono
            const base64 = audioToBase64Wav(audio, 16000);
            dotNetRef?.invokeMethodAsync('OnVadSpeechEnd', base64);
        }
    });
}

// Pauses VAD listening (used during TTS playback to prevent echo loops).
export function pause() {
    micVad?.pause();
}

// Resumes VAD listening after TTS playback finishes.
export function resume() {
    micVad?.start();
}

// Destroys the VAD instance and releases microphone resources.
export async function stop() {
    if (!micVad) return;
    micVad.destroy();
    micVad = null;
    dotNetRef = null;
}

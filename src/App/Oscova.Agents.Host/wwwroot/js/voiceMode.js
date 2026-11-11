// Voice Mode — continuous voice conversation using @ricky0123/vad-web (ES module)
// VAD detects speech segments and streams WAV audio to .NET for transcription.
// Usage from Blazor: const mod = await import('/js/voiceMode.js'); await mod.start(dotNetRef); mod.pause(); mod.resume(); await mod.stop();
import { encodeWav } from '/js/audioRecorder.js';

const VadAssetPath = '/lib/vad/';

let dotNetRef = null;
let micVad = null;
let vadLibraryLoaded = false;
let startGeneration = 0;

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

// Creates and starts the MicVAD instance. Calls dotNetRef.OnVadSpeechEnd with a WAV stream on each utterance.
// Returns false when a stop or a newer start superseded this one while it waited for the microphone.
export async function start(ref) {
    if (micVad) return true;
    const generation = ++startGeneration;
    dotNetRef = ref;

    await loadVadLibrary();

    const vad = await window.vad.MicVAD.new({
        baseAssetPath: VadAssetPath,
        onnxWASMBasePath: VadAssetPath,
        startOnLoad: true,


        onSpeechStart: () => {
            dotNetRef?.invokeMethodAsync('OnVadSpeechStart');
        },

        onSpeechEnd: (audio) => {
            // Stream 16kHz mono audio so long utterances do not exceed the circuit's message limit.
            if (dotNetRef) {
                const stream = DotNet.createJSStreamReference(new Blob([encodeWav(audio, 16000, 1)], { type: 'audio/wav' }));
                dotNetRef.invokeMethodAsync('OnVadSpeechEnd', stream);
            }
        }
    });

    // A stop during startup wins, so a microphone granted after leaving Chat never keeps listening.
    if (generation !== startGeneration) {
        await vad.destroy();
        return false;
    }

    micVad = vad;
    return true;
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
    startGeneration++;
    if (!micVad) return;
    micVad.destroy();
    micVad = null;
    dotNetRef = null;
}

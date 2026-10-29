// Browser-side microphone recorder that returns 16kHz mono WAV as a Blob via JS interop streaming (ES module)
// Usage from Blazor: const mod = await import('/js/audioRecorder.js'); await mod.start(); const blob = await mod.stop();
let audioCtx = null;
let mediaStream = null;
let sourceNode = null;
let processor = null; // ScriptProcessorNode or AudioWorkletNode
let silentGainNode = null; // Used only for AudioWorkletNode path to keep graph alive without feedback
let recording = false;
let captureController = null;
let buffers = [];
let inputSampleRate = 48000;

export async function start() {
    if (captureController) { return; }
    const capture = new AbortController();
    captureController = capture;
    buffers = [];
    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
        // Permission can arrive after the chat that requested it has been disposed.
        if (capture.signal.aborted) { stream.getTracks().forEach(t => t.stop()); return; }
        mediaStream = stream;
        audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        inputSampleRate = audioCtx.sampleRate || 48000;
        sourceNode = audioCtx.createMediaStreamSource(mediaStream);

        // Prefer AudioWorkletNode (modern, non-deprecated). Fallback to ScriptProcessorNode for older browsers.
        const supportsWorklet = !!(audioCtx.audioWorklet) && 'AudioWorkletNode' in window;
        if (supportsWorklet) {
            try {
                await audioCtx.audioWorklet.addModule('/js/recorder-worklet.js');
                if (capture.signal.aborted) { return; }
                processor = new AudioWorkletNode(audioCtx, 'recorder-processor', {
                    numberOfInputs: 1,
                    numberOfOutputs: 1, // one silent output to keep the graph connected
                    outputChannelCount: [1]
                });
                processor.port.onmessage = (e) => {
                    if (!recording) { return; }
                    // e.data is a Float32Array (transferable) of mono samples
                    const chData = e.data;
                    buffers.push(new Float32Array(chData));
                };
                sourceNode.connect(processor);
                // Send to a silent gain node so processing keeps running without feedback to speakers
                silentGainNode = audioCtx.createGain();
                silentGainNode.gain.value = 0;
                processor.connect(silentGainNode);
                silentGainNode.connect(audioCtx.destination);
                recording = true;
                return;
            } catch (err) {
                if (capture.signal.aborted) { return; }
                console.warn('[audioRecorder] AudioWorkletNode failed to initialize, falling back to ScriptProcessorNode', err);
            }
        }

        // ScriptProcessorNode fallback (deprecated but widely supported)
        const bufferSize = 4096;
        processor = audioCtx.createScriptProcessor(bufferSize, 1, 1);
        processor.onaudioprocess = function (e) {
            if (!recording) { return; }
            const chData = e.inputBuffer.getChannelData(0);
            // Copy into a new Float32Array to avoid detaching
            buffers.push(new Float32Array(chData));
        };
        sourceNode.connect(processor);
        processor.connect(audioCtx.destination);
        recording = true;
    } catch (err) {
        if (capture.signal.aborted) { return; }
        await stop();
        console.error('[audioRecorder] getUserMedia or initialization failed', err);
        throw err;
    }
}

export async function stop() {
    captureController?.abort();
    captureController = null;
    recording = false;
    const capturedBuffers = buffers;
    const capturedSampleRate = inputSampleRate;
    const capturedContext = audioCtx;
    try {
        // Stop audio graph
        if (processor) {
            try {
                if (processor.port && processor.port.onmessage) { processor.port.onmessage = null; }
            } catch { }
            try { processor.disconnect(); } catch { }
        }
        if (silentGainNode) {
            try { silentGainNode.disconnect(); } catch { }
        }
        if (sourceNode) { try { sourceNode.disconnect(); } catch { } }
        if (mediaStream) {
            mediaStream.getTracks().forEach(t => { try { t.stop(); } catch { } });
        }
    } catch { }
    cleanup();
    if (capturedContext && capturedContext.state !== 'closed') { try { await capturedContext.close(); } catch { } }

    // Merge buffers
    const totalLen = capturedBuffers.reduce((acc, b) => acc + b.length, 0);
    if (totalLen === 0) { return null; }
    const merged = new Float32Array(totalLen);
    let offset = 0;
    for (const b of capturedBuffers) { merged.set(b, offset); offset += b.length; }

    // Downsample to 16kHz mono
    const targetRate = 16000;
    const downsampled = downsampleBuffer(merged, capturedSampleRate, targetRate);
    if (!downsampled || downsampled.length === 0) { return null; }

    // Encode to 16-bit PCM WAV and return as Blob (to be streamed to .NET)
    const wavBytes = encodeWav(downsampled, targetRate, 1);
    const blob = new Blob([wavBytes], { type: 'audio/wav' });

    return blob;
}

function cleanup() {
    // Help GC
    audioCtx = null;
    mediaStream = null;
    sourceNode = null;
    processor = null;
    silentGainNode = null;
    buffers = [];
}

function downsampleBuffer(buffer, sampleRate, outSampleRate) {
    if (outSampleRate === sampleRate) {
        return buffer;
    }
    if (outSampleRate > sampleRate) {
        console.warn('[audioRecorder] Upsampling not supported; returning original');
        return buffer;
    }
    const ratio = sampleRate / outSampleRate;
    const newLen = Math.floor(buffer.length / ratio);
    const result = new Float32Array(newLen);
    let pos = 0;
    let idx = 0;
    while (idx < newLen) {
        const nextPos = Math.round((idx + 1) * ratio);
        // Average samples in this range to anti-alias
        let sum = 0;
        let count = 0;
        while (pos < nextPos && pos < buffer.length) {
            sum += buffer[pos++];
            count++;
        }
        result[idx++] = sum / (count || 1);
    }
    return result;
}

export function encodeWav(samples, sampleRate, numChannels) {
    const bytesPerSample = 2;
    const blockAlign = numChannels * bytesPerSample;
    const byteRate = sampleRate * blockAlign;
    const dataSize = samples.length * bytesPerSample;
    const buffer = new ArrayBuffer(44 + dataSize);
    const view = new DataView(buffer);

    // RIFF chunk descriptor
    writeString(view, 0, 'RIFF');
    view.setUint32(4, 36 + dataSize, true); // ChunkSize
    writeString(view, 8, 'WAVE');

    // fmt subchunk
    writeString(view, 12, 'fmt ');
    view.setUint32(16, 16, true); // Subchunk1Size
    view.setUint16(20, 1, true); // PCM
    view.setUint16(22, numChannels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, 16, true); // bits per sample

    // data subchunk
    writeString(view, 36, 'data');
    view.setUint32(40, dataSize, true);

    // PCM samples
    floatTo16BitPCM(view, 44, samples);
    return buffer;
}

function writeString(view, offset, str) {
    for (let i = 0; i < str.length; i++) {
        view.setUint8(offset + i, str.charCodeAt(i));
    }
}

function floatTo16BitPCM(view, offset, input) {
    for (let i = 0; i < input.length; i++, offset += 2) {
        let s = Math.max(-1, Math.min(1, input[i]));
        view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
    }
}

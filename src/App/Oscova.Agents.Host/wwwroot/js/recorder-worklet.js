// recorder-worklet.js - AudioWorkletProcessor that forwards input mono samples to the main thread
// This file must be served as a separate script and loaded via audioCtx.audioWorklet.addModule('/js/recorder-worklet.js')

class RecorderProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    // No state needed; we stream per-render quantum (128 frames) as Float32Array via postMessage
  }

  process(inputs, outputs, parameters) {
    const input = inputs[0];
    if (input && input.length > 0) {
      const ch0 = input[0]; // Float32Array of 128 frames
      // Clone to a transferable buffer to avoid detaching original
      const copy = new Float32Array(ch0.length);
      copy.set(ch0);
      // Transfer underlying ArrayBuffer for efficiency
      this.port.postMessage(copy, [copy.buffer]);
    }

    // Pass through silence to output to keep the graph connected
    const output = outputs[0];
    if (output && output.length > 0) {
      const outCh0 = output[0];
      outCh0.fill(0);
    }
    // Keep processor alive
    return true;
  }
}

registerProcessor('recorder-processor', RecorderProcessor);

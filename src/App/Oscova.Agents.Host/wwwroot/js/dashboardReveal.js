// Dashboard-only reveal overlay: this ports the provided shader and adds alpha
// so the loaded chat workspace becomes visible from the center outward.
const handles = new Map();

const VERTEX_SHADER = `
attribute vec2 aPos;
void main() {
    gl_Position = vec4(aPos, 0.0, 1.0);
}`;

const FRAGMENT_SHADER = `
#define TWO_PI 6.2831853072
#define PI 3.14159265359

precision highp float;
uniform vec2 uResolution;
uniform float uProgress;
uniform float uTime;

void main() {
    vec2 uv = (gl_FragCoord.xy * 2.0 - uResolution.xy) / min(uResolution.x, uResolution.y);
    float radius = length(uv);
    float t = uTime * 0.05;
    float lineWidth = 0.002;

    vec3 color = vec3(0.0);
    for (int j = 0; j < 3; j++) {
        for (int i = 0; i < 5; i++) {
            color[j] += lineWidth * float(i * i) /
                abs(fract(t - 0.01 * float(j) + float(i) * 0.01) * 5.0 - radius + mod(uv.x + uv.y, 0.2));
        }
    }

    float reveal = mix(0.01, 1.72, smoothstep(0.0, 1.0, uProgress));
    float revealMask = smoothstep(reveal - 0.08, reveal + 0.08, radius);
    float glow = clamp(max(max(color.r, color.g), color.b), 0.0, 1.0);
    float diagonal = smoothstep(0.015, 0.19, mod(uv.x + uv.y, 0.2));
    vec3 darkBand = mix(vec3(0.005, 0.006, 0.007), vec3(0.09, 0.11, 0.13), diagonal * 0.48);
    float finalFade = 1.0 - smoothstep(0.86, 1.0, uProgress);

    color = darkBand * revealMask + color;
    float alpha = clamp(revealMask * 0.94 + glow * 0.88, 0.0, 0.98) * finalFade;
    gl_FragColor = vec4(color, alpha);
}`;

export function playDashboardReveal(overlayId) {
    disposeDashboardReveal(overlayId);

    const overlay = document.getElementById(overlayId);
    if (!overlay) {
        return;
    }

    overlay.classList.add("dashboard-reveal-running");

    if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        finish(overlay, overlayId);
        return;
    }

    const canvas = document.createElement("canvas");
    canvas.className = "dashboard-reveal-canvas";
    overlay.appendChild(canvas);

    const gl = canvas.getContext("webgl", {
        alpha: true,
        premultipliedAlpha: false,
        antialias: true,
    });

    if (!gl) {
        finish(overlay, overlayId);
        return;
    }

    const program = buildProgram(gl);
    if (!program) {
        finish(overlay, overlayId);
        return;
    }

    gl.useProgram(program);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    const quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);

    const aPos = gl.getAttribLocation(program, "aPos");
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);

    const uResolution = gl.getUniformLocation(program, "uResolution");
    const uProgress = gl.getUniformLocation(program, "uProgress");
    const uTime = gl.getUniformLocation(program, "uTime");
    const start = performance.now();
    const durationMs = 1900;
    const handle = {
        frame: 0,
        disposed: false,
        cleanup: () => {
            handle.disposed = true;
            if (handle.frame) {
                cancelAnimationFrame(handle.frame);
            }
            gl.deleteBuffer(quad);
            gl.deleteProgram(program);
            canvas.remove();
        },
    };

    handles.set(overlayId, handle);

    const resize = () => {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const width = Math.max(1, Math.round(window.innerWidth * dpr));
        const height = Math.max(1, Math.round(window.innerHeight * dpr));
        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
            gl.viewport(0, 0, width, height);
        }
        gl.uniform2f(uResolution, width, height);
    };

    const render = (now) => {
        if (handle.disposed) {
            return;
        }

        resize();
        const elapsed = now - start;
        const progress = clamp(elapsed / durationMs, 0, 1);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.uniform1f(uProgress, progress);
        gl.uniform1f(uTime, progress * 6.2 + elapsed / 2400);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

        if (progress >= 1) {
            finish(overlay, overlayId);
            return;
        }

        handle.frame = requestAnimationFrame(render);
    };

    handle.frame = requestAnimationFrame(render);
}

export function disposeDashboardReveal(overlayId) {
    const handle = handles.get(overlayId);
    if (!handle) {
        return;
    }

    handle.cleanup();
    handles.delete(overlayId);
}

function finish(overlay, overlayId) {
    disposeDashboardReveal(overlayId);
    overlay.classList.add("dashboard-reveal-finished");
    window.setTimeout(() => overlay.remove(), 320);
}

function buildProgram(gl) {
    const vertex = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER);
    const fragment = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER);
    if (!vertex || !fragment) {
        if (vertex) {
            gl.deleteShader(vertex);
        }
        if (fragment) {
            gl.deleteShader(fragment);
        }
        return null;
    }

    const program = gl.createProgram();
    gl.attachShader(program, vertex);
    gl.attachShader(program, fragment);
    gl.linkProgram(program);
    gl.deleteShader(vertex);
    gl.deleteShader(fragment);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return null;
    }

    return program;
}

function compileShader(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);

    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        gl.deleteShader(shader);
        return null;
    }

    return shader;
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

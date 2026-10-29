const instances = new WeakMap();
const policy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
const attribute = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

// The trusted outer frame's policy also restricts replacement navigation of the generated inner frame.
export function render(container, html, title) {
    // Navigation can remove the element while its server-side module import is pending.
    if (!container?.isConnected) { return; }
    dispose(container);
    const outer = document.createElement('iframe');
    outer.title = title;
    outer.setAttribute('sandbox', 'allow-scripts');
    outer.setAttribute('referrerpolicy', 'no-referrer');
    const load = () => {
        const style = getComputedStyle(container);
        const scheme = getComputedStyle(document.documentElement).colorScheme;
        // Read the theme token before the chat's animated color transition finishes.
        const color = style.getPropertyValue('--color-base-content').trim() || style.color;
        outer.style.colorScheme = scheme;
        const defaults = `<style>:root{color-scheme:${scheme};color:${color};font-family:system-ui,sans-serif}html,body{background:transparent}body{margin:0}*{box-sizing:border-box}img,svg,canvas{max-width:100%}@media(prefers-reduced-motion:reduce){*,*::before,*::after{animation:none!important;transition:none!important}}</style>`;
        // Standards mode and body measurement let collapsed content shrink below the previous viewport height.
        const resize = `<script>addEventListener('load',()=>{let previous;const send=()=>{const height=Math.ceil(Math.max(document.body.scrollHeight,document.body.getBoundingClientRect().height));if(height===previous){return}previous=height;parent.postMessage({type:'visualization-size',height},'*')};new ResizeObserver(send).observe(document.body);send()})<\/script>`;
        const inner = `<!doctype html><meta http-equiv="Content-Security-Policy" content="${policy}">${defaults}${resize}${html}`;
        // Listen before creating the child so its first size message cannot arrive before the handler.
        outer.srcdoc = `<!doctype html><meta http-equiv="Content-Security-Policy" content="${policy}"><style>:root{color-scheme:${scheme}}html,body{background:transparent}body{margin:0}iframe{color-scheme:${scheme};width:100%;height:180px;border:0;display:block}</style><script>addEventListener('message',e=>{const frame=document.querySelector('iframe');if(!frame||e.source!==frame.contentWindow||e.data?.type!=='visualization-size'||!Number.isFinite(e.data.height))return;const height=Math.max(180,Math.min(2400,e.data.height));frame.style.height=height+'px';parent.postMessage({type:'visualization-size',height},'*')})<\/script><iframe title="${attribute(title)}" sandbox="allow-scripts" referrerpolicy="no-referrer" srcdoc="${attribute(inner)}"></iframe>`;
    };
    const resize = event => {
        if (event.source !== outer.contentWindow || event.data?.type !== 'visualization-size' || !Number.isFinite(event.data.height)) { return; }
        outer.style.height = `${Math.max(180, Math.min(2400, event.data.height))}px`;
    };
    // Recreate the frame because browsers retain its initial canvas color across color-scheme changes.
    const theme = new MutationObserver(() => {
        if (getComputedStyle(document.documentElement).colorScheme !== outer.style.colorScheme) {
            render(container, html, title);
        }
    });
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'style', 'class'] });
    window.addEventListener('message', resize);
    instances.set(container, () => { theme.disconnect(); window.removeEventListener('message', resize); outer.remove(); });
    load();
    container.replaceChildren(outer);
}

export function dispose(container) {
    instances.get(container)?.();
    instances.delete(container);
}

import { copyToClipboard } from './utils.js';

// Markdown is sanitized by the server. These controls add no author-supplied HTML.
export function enhance(root, dotNet, labels) {
    // Streaming can replace the rendered root before this browser call arrives.
    if (!root) { return null; }
    const stopped = new AbortController();
    const timers = new Set();

    function button(label, icon) {
        const element = document.createElement('button');
        element.type = 'button';
        element.className = 'btn btn-ghost btn-xs btn-square';
        element.title = label;
        element.setAttribute('aria-label', label);
        const glyph = document.createElement('iconify-icon');
        glyph.setAttribute('icon', icon);
        glyph.setAttribute('width', '14');
        glyph.setAttribute('height', '14');
        glyph.setAttribute('aria-hidden', 'true');
        element.append(glyph);
        return element;
    }

    const tables = Array.from(root.querySelectorAll('table'));
    for (const block of root.querySelectorAll('table, pre')) {
        const isTable = block.tagName === 'TABLE';
        const wrapper = document.createElement('div');
        wrapper.className = 'custom-chat-block';
        const toolbar = document.createElement('div');
        toolbar.className = 'custom-chat-block-toolbar';
        const code = isTable ? null : block.querySelector('code');
        const language = Array.from(code?.classList ?? []).find(value => value.startsWith('language-'))?.slice(9);
        if (language) {
            const label = document.createElement('span');
            label.className = 'custom-chat-code-language';
            label.textContent = language;
            toolbar.append(label);
        }

        const copy = button(labels.copy, 'lucide:copy');
        copy.setAttribute('aria-live', 'polite');
        copy.addEventListener('click', async () => {
            // Read on click so each block copies the current displayed text, never its toolbar.
            const text = isTable
                ? Array.from(block.rows, row => Array.from(row.cells,
                    cell => cell.innerText.replace(/[\r\n\t]+/g, ' ').trim()).join('\t')).join('\n')
                : (code ?? block).textContent;
            if (!await copyToClipboard(text)) {
                await dotNet.invokeMethodAsync('CopyFailed');
                return;
            }
            if (stopped.signal.aborted) { return; }
            const glyph = copy.querySelector('iconify-icon');
            glyph.setAttribute('icon', 'lucide:check');
            copy.classList.add('text-success');
            copy.title = labels.copied;
            copy.setAttribute('aria-label', labels.copied);
            copy.disabled = true;
            const timer = setTimeout(() => {
                timers.delete(timer);
                glyph.setAttribute('icon', 'lucide:copy');
                copy.classList.remove('text-success');
                copy.title = labels.copy;
                copy.setAttribute('aria-label', labels.copy);
                copy.disabled = false;
            }, 2000);
            timers.add(timer);
        }, { signal: stopped.signal });
        toolbar.append(copy);

        if (isTable) {
            const expand = button(labels.expand, 'lucide:maximize-2');
            expand.addEventListener('click', () => dotNet.invokeMethodAsync('ExpandTable', tables.indexOf(block)),
                { signal: stopped.signal });
            toolbar.append(expand);
        }

        block.before(wrapper);
        const scroll = document.createElement('div');
        scroll.className = 'custom-chat-block-scroll';
        scroll.append(block);
        wrapper.append(toolbar, scroll);
    }

    return {
        dispose() {
            stopped.abort();
            for (const timer of timers) { clearTimeout(timer); }
            timers.clear();
        }
    };
}

import {
    createEditor,
    insertBefore,
    insertText as insertEditorText,
    languages,
    Token
} from "/lib/prism-code-editor/prism-code-editor.bundle.js";

const handles = new Map();
const observers = new Map();
let nextHandle = 1;
const variablePattern = /\{\{\s*[\w.-]+\s*\}\}/g;
const variableGrammarToken = {
    pattern: variablePattern
};

installVariableGrammars();

export function create(container, options) {
    if (!container) {
        return 0;
    }

    const handle = nextHandle++;
    const editor = createEditor(container, {
        language: normalizeLanguage(options?.language),
        value: options?.value ?? "",
        readOnly: !!options?.readOnly,
        lineNumbers: false,
        wordWrap: true,
        class: "oscova-code-editor-instance",
        onTokenize: options?.highlightVariables ? tokenizeVariables : undefined
    });

    editor.textarea.setAttribute("aria-label", options.label);
    handles.set(handle, editor);
    if (options?.observer) {
        const state = { baseline: options.baseline ?? options.value ?? "", dirty: false, observer: options.observer };
        state.dirty = editor.value.replace(/\r\n/g, "\n") !== state.baseline.replace(/\r\n/g, "\n");
        state.notify = () => {
            const dirty = editor.value.replace(/\r\n/g, "\n") !== state.baseline.replace(/\r\n/g, "\n");
            if (dirty !== state.dirty) {
                state.dirty = dirty;
                state.observer.invokeMethodAsync("NotifyDirtyAsync", dirty).catch(() => {});
            }
        };
        editor.textarea.addEventListener("input", state.notify);
        observers.set(handle, state);
    }
    return handle;
}

export function setLabel(handle, label) {
    handles.get(handle)?.textarea.setAttribute("aria-label", label);
}

export function setBaseline(handle, baseline) {
    const state = observers.get(handle);
    if (state) { state.baseline = baseline ?? ""; state.notify(); }
}

export function getValue(handle) {
    return handles.get(handle)?.value ?? "";
}

export function setValue(handle, value) {
    const editor = handles.get(handle);
    const nextValue = value ?? "";

    if (!editor || editor.value === nextValue) {
        return;
    }

    editor.setOptions({ value: nextValue });
    observers.get(handle)?.notify();
}

export function setLanguage(handle, language) {
    handles.get(handle)?.setOptions({ language: normalizeLanguage(language) });
}

export function setReadOnly(handle, readOnly) {
    handles.get(handle)?.setOptions({ readOnly: !!readOnly });
}

export function setHighlightVariables(handle, highlightVariables) {
    handles.get(handle)?.setOptions({
        onTokenize: highlightVariables ? tokenizeVariables : undefined
    });
}

export function insertText(handle, text) {
    const editor = handles.get(handle);
    if (!editor) {
        return "";
    }

    insertEditorText(editor, text ?? "");
    observers.get(handle)?.notify();
    return editor.value;
}

export function focus(handle) {
    handles.get(handle)?.textarea?.focus();
}

export function dispose(handle) {
    const editor = handles.get(handle);
    if (!editor) {
        return;
    }

    const state = observers.get(handle);
    if (state) { editor.textarea.removeEventListener("input", state.notify); }
    observers.delete(handle);
    editor.remove();
    handles.delete(handle);
}

function normalizeLanguage(language) {
    switch ((language ?? "").toLowerCase()) {
        case "json":
            return "json";
        case "yaml":
        case "yml":
            return "yaml";
        case "markdown":
        case "md":
            return "markdown";
        case "xml":
            return "xml";
        default:
            return "text";
    }
}

function installVariableGrammars() {
    installVariableGrammar(languages.markdown, "markup-bracket");
    installVariableGrammar(languages.yaml, "string");
    installVariableGrammar(languages.json, "property");
    installVariableGrammar(languages.markup, "tag");
}

function installVariableGrammar(grammar, beforeToken) {
    if (!grammar || grammar["prompt-variable"]) {
        return;
    }

    if (grammar[beforeToken]) {
        insertBefore(grammar, beforeToken, { "prompt-variable": variableGrammarToken });
        return;
    }

    grammar["prompt-variable"] = variableGrammarToken;
}

function tokenizeVariables(tokens) {
    replaceVariables(tokens);
}

function replaceVariables(tokens) {
    for (let index = 0; index < tokens.length; index++) {
        const token = tokens[index];

        if (typeof token === "string") {
            const replacements = buildVariableTokens(token);
            if (replacements.length > 1 || replacements[0] !== token) {
                tokens.splice(index, 1, ...replacements);
                index += replacements.length - 1;
            }

            continue;
        }

        if (token.type === "prompt-variable") {
            continue;
        }

        if (Array.isArray(token.content)) {
            replaceVariables(token.content);
            continue;
        }

        if (typeof token.content !== "string") {
            continue;
        }

        const replacements = buildVariableTokens(token.content);
        if (replacements.length > 1 || replacements[0] !== token.content) {
            token.content = replacements;
        }
    }
}

function buildVariableTokens(text) {
    const result = [];
    let position = 0;
    let match;

    variablePattern.lastIndex = 0;
    while ((match = variablePattern.exec(text)) !== null) {
        if (match.index > position) {
            result.push(text.slice(position, match.index));
        }

        result.push(new Token("prompt-variable", match[0], match[0]));
        position = match.index + match[0].length;
    }

    if (position < text.length) {
        result.push(text.slice(position));
    }

    return result.length === 0 ? [text] : result;
}

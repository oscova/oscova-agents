import { closeDropdowns } from './utils.js';

export function showModal(elementId, dotNetRef) {
    let element = document.getElementById(elementId);
    if (!element) {
        console.error("Element with ID '" + elementId + "' not found.");
        return;
    }

    element.addEventListener('click', handleButtonClick);
    element.addEventListener('keydown', handleDialogKeyDown);

    // The browser fires 'cancel' on Escape. preventDefault stops the native close so the .NET
    // side stays authoritative over the open flag, then the callback runs the close path.
    // Whether the dialog may actually close is decided there, so no state is copied down here.
    // Callers without a dotNetRef (HelpPanel) own Escape through their own @onkeydown handler.
    const cancelHandler = (event) => {
        event.preventDefault();
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnDialogCancelled');
        }
    };
    element._cancelHandler = cancelHandler;
    element.addEventListener('cancel', cancelHandler);

    // Remember the visible menu trigger rather than a row the modal will hide.
    closeDropdowns();
    element.showModal();
}

export function closeModal(elementId) {
    let element = document.getElementById(elementId);
    if (!element) {
        console.error("Element with ID '" + elementId + "' not found.");
        return;
    }

    element.removeEventListener('click', handleButtonClick);
    element.removeEventListener('keydown', handleDialogKeyDown);
    if (element._cancelHandler) {
        element.removeEventListener('cancel', element._cancelHandler);
        delete element._cancelHandler;
    }
    element.close();
}

export function waitForAnimationFrame() {
    return new Promise(resolve => requestAnimationFrame(resolve));
}

export function setTheme(themeName) {
    document.documentElement.setAttribute('data-theme', themeName);
}

// Returns focus to a trigger that lives outside the calling component's render tree.
export function focusElement(elementId) {
    document.getElementById(elementId)?.focus();
}

export function scrollElementIntoView(elementId) {
    document.getElementById(elementId)?.scrollIntoView({ block: "nearest", inline: "nearest" });
}

function handleDialogKeyDown(event) {
    // An open combobox owns the first Escape so its unsaved editor stays open.
    if (event.key === 'Escape' && event.target.matches('[role="combobox"][aria-expanded="true"]')) {
        event.preventDefault();
    }
}

function handleButtonClick(event) {
    // Keep .NET authoritative for form submission without suppressing native popover buttons.
    if (event.target.closest('button')?.type === 'submit') {
        event.preventDefault();
    }
}

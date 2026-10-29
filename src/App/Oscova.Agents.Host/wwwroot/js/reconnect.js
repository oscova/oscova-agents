{
    const dialog = document.getElementById("components-reconnect-modal");
    const title = document.getElementById("custom-reconnect-title");
    const defaultTitle = title.textContent;
    const status = document.getElementById("custom-reconnect-status");
    const countdown = document.getElementById("custom-reconnect-countdown");
    const spinner = document.getElementById("custom-reconnect-spinner");
    const retryButton = document.getElementById("custom-reconnect-retry");
    const resumeButton = document.getElementById("custom-reconnect-resume");
    const reloadButton = document.getElementById("custom-reconnect-reload");
    let state = "hide";
    let inFlight = false;
    let previousFocus;

    function retryWhenVisible() {
        if (document.visibilityState === "visible") {
            recover("retry");
        }
    }

    function display(nextState, secondsToNextAttempt = 0) {
        state = nextState;
        document.removeEventListener("visibilitychange", retryWhenVisible);
        if (state === "hide") {
            if (dialog.open) {
                dialog.close();
                if (previousFocus?.isConnected) {
                    previousFocus.focus();
                }
            }
            previousFocus = undefined;
            return;
        }

        if (!dialog.open) {
            previousFocus = document.activeElement;
            dialog.showModal();
        }

        // An application update restarts the server, and its monitor reloads the page once it returns.
        const update = dialog.dataset.updateMessage;
        if (update) {
            const failed = dialog.dataset.updateFailed === "true";
            title.textContent = update;
            status.hidden = false;
            status.textContent = dialog.dataset.updating;
            countdown.hidden = true;
            spinner.hidden = failed;
            retryButton.hidden = true;
            resumeButton.hidden = true;
            reloadButton.hidden = !failed;
            return;
        }

        const retrying = state === "retrying" && secondsToNextAttempt > 0;
        const connecting = state === "show" || state === "retrying";
        title.textContent = connecting ? dialog.dataset.connecting : defaultTitle;
        status.hidden = connecting;
        const action = state === "failed" ? retryButton
            : state === "paused" || state === "resume-failed" ? resumeButton
                : state === "rejected" ? reloadButton : null;
        const wasAvailable = action && !action.hidden && !action.disabled;

        status.textContent = state === "failed" ? dialog.dataset.failed
            : state === "paused" ? dialog.dataset.paused
                : state === "resume-failed" ? dialog.dataset.resumeFailed
                    : state === "rejected" ? dialog.dataset.expired
                        : state === "resuming" ? dialog.dataset.resuming
                            : "";
        countdown.hidden = !retrying;
        countdown.textContent = retrying
            ? dialog.dataset.countdown.replace("{0}", secondsToNextAttempt)
            : "";
        spinner.hidden = !["show", "retrying", "resuming"].includes(state);
        retryButton.hidden = action !== retryButton;
        resumeButton.hidden = action !== resumeButton;
        reloadButton.hidden = action !== reloadButton;
        retryButton.disabled = inFlight;
        resumeButton.disabled = inFlight;

        if (state === "failed") {
            document.addEventListener("visibilitychange", retryWhenVisible);
        }
        if (action && !inFlight && (!wasAvailable || !dialog.contains(document.activeElement))) {
            action.focus();
        }
    }

    async function recover(kind) {
        if (inFlight) {
            return;
        }

        inFlight = true;
        display(kind === "retry" ? "show" : "resuming");
        try {
            if (kind === "retry") {
                // A lost circuit can still be resumed after a reconnect returns false.
                const connected = await Blazor.reconnect();
                const recovered = connected || await Blazor.resumeCircuit();
                display(recovered ? "hide" : "rejected");
            } else {
                display(await Blazor.resumeCircuit() ? "hide" : "rejected");
            }
        } catch (error) {
            console.error(kind === "retry" ? "Reconnect failed" : "Resume failed", error);
            if (state !== "hide") {
                display(kind === "retry" ? "failed" : "resume-failed");
            }
        } finally {
            inFlight = false;
            if (state !== "hide") {
                display(state);
            }
        }
    }

    dialog.addEventListener("cancel", event => { event.preventDefault(); });
    dialog.addEventListener("oscova-update-message", () => {
        if (state !== "hide") {
            display(state);
        }
    });
    dialog.addEventListener("components-reconnect-state-changed", event => {
        // Blazor owns attempts and timing. This updates presentation only.
        display(event.detail.state, event.detail.secondsToNextAttempt);
    });
    retryButton.addEventListener("click", () => { recover("retry"); });
    resumeButton.addEventListener("click", () => { recover("resume"); });
    reloadButton.addEventListener("click", () => { location.reload(); });
}

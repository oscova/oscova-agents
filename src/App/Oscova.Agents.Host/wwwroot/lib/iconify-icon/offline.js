// Disable Iconify's API fetch path after local preloaded collections are registered.
customElements.whenDefined("iconify-icon").then(function () {
    var iconifyIcon = customElements.get("iconify-icon");
    if (iconifyIcon && iconifyIcon._api && iconifyIcon._api.setFetch) {
        iconifyIcon._api.setFetch(undefined);
    }
});

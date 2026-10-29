// Layout customizer for Blazor JS interop
// Manages theme, font, sidebar theme, direction, fullscreen, and wallpaper effects via localStorage

const STORAGE_KEY = "__OSCOVA_LAYOUT__";

const DEFAULT_CONFIG = {
    theme: "system",
    fontFamily: "default",
    sidebarTheme: "light",
    direction: "ltr",
    fullscreen: false,
    wallpaperEffectsEnabled: true,
};

export function getConfig() {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored) {
            return { ...DEFAULT_CONFIG, ...JSON.parse(stored) };
        }
    } catch { }
    return { ...DEFAULT_CONFIG };
}

export function setConfig(config) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(config));
    applyConfig(config);
}

export function applyConfig(config) {
    const html = document.documentElement;

    // Theme
    if (config.theme === "system") {
        html.removeAttribute("data-theme");
    } else {
        html.setAttribute("data-theme", config.theme);
    }

    // Sidebar theme
    html.setAttribute("data-sidebar-theme", config.sidebarTheme);
    const sidebar = document.getElementById("layout-sidebar");
    if (sidebar) {
        if (config.sidebarTheme === "dark" && config.theme === "light") {
            sidebar.setAttribute("data-theme", config.sidebarTheme);
        } else {
            sidebar.removeAttribute("data-theme");
        }
    }

    // Font family
    if (config.fontFamily !== "default") {
        html.setAttribute("data-font-family", config.fontFamily);
    } else {
        html.removeAttribute("data-font-family");
    }

    // Direction
    html.dir = config.direction;

    // Fullscreen
    if (config.fullscreen) {
        html.setAttribute("data-fullscreen", "");
    } else {
        html.removeAttribute("data-fullscreen");
    }

    // Wallpaper and glass effects
    if (config.wallpaperEffectsEnabled === false) {
        html.setAttribute("data-wallpaper-effects", "off");
    } else {
        html.removeAttribute("data-wallpaper-effects");
    }

    // Changed indicator (for reset button dot)
    const isChanged = JSON.stringify(config) !== JSON.stringify(DEFAULT_CONFIG);
    if (isChanged) {
        html.setAttribute("data-changed", "");
    } else {
        html.removeAttribute("data-changed");
    }
}

export function resetConfig() {
    const config = { ...DEFAULT_CONFIG };
    setConfig(config);
    return config;
}

export function setTheme(theme) {
    const config = getConfig();
    config.theme = theme;
    setConfig(config);
    return config;
}

// System follows the OS until the user chooses an explicit appearance.
export function toggleTheme() {
    const config = getConfig();
    const isDark = config.theme === "dark" ||
        (config.theme === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
    return setTheme(isDark ? "light" : "dark");
}

export function setFontFamily(fontFamily) {
    const config = getConfig();
    config.fontFamily = fontFamily;
    setConfig(config);
    return config;
}

export function setSidebarTheme(sidebarTheme) {
    const config = getConfig();
    config.sidebarTheme = sidebarTheme;
    setConfig(config);
    return config;
}

export function setDirection(direction) {
    const config = getConfig();
    config.direction = direction;
    setConfig(config);
    return config;
}

export function setWallpaperEffectsEnabled(enabled) {
    const config = getConfig();
    config.wallpaperEffectsEnabled = enabled;
    setConfig(config);
    return config;
}

export async function toggleFullscreen() {
    const config = getConfig();
    try {
        if (!document.fullscreenElement) {
            await document.documentElement.requestFullscreen();
            config.fullscreen = true;
        } else {
            await document.exitFullscreen();
            config.fullscreen = false;
        }
    } catch { }
    setConfig(config);
    return config;
}

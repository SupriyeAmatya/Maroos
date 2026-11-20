// Light and dark mode. The page's theme is Bootstrap's data-bs-theme attribute on <html> ("light" or "dark").
// A choice made with the top bar's toggle is kept in this browser's localStorage; without one, the page follows the
// device's setting and keeps following it if that changes. This file loads in <head> before the stylesheets, so a
// dark page is dark from the first paint.
(() => {
    const key = "maroos.theme";
    const deviceDark = window.matchMedia?.("(prefers-color-scheme: dark)");

    const saved = () => {
        try {
            const value = localStorage.getItem(key);
            return value === "dark" || value === "light" ? value : null;
        } catch {
            return null; // storage blocked: follow the device
        }
    };

    const save = theme => {
        try {
            localStorage.setItem(key, theme);
        } catch {
            // Can't remember it, but the click still applies for this page.
        }
    };

    const current = () => saved() ?? (deviceDark?.matches ? "dark" : "light");

    const apply = theme => {
        document.documentElement.setAttribute("data-bs-theme", theme);
        document.querySelectorAll("[data-theme-toggle]").forEach(button => button.setAttribute("aria-pressed", String(theme === "dark")));
    };

    apply(current());

    // No choice saved yet: follow the device when its setting changes.
    deviceDark?.addEventListener?.("change", () => {
        if (!saved())
            apply(current());
    });

    document.addEventListener("click", event => {
        if (!event.target.closest?.("[data-theme-toggle]"))
            return;
        const next = document.documentElement.getAttribute("data-bs-theme") === "dark" ? "light" : "dark";
        save(next);
        apply(next);
    });

    document.addEventListener("DOMContentLoaded", () => {
        apply(current());
        // Blazor's enhanced navigation swaps pages without a full load and resets the server-rendered markup.
        window.Blazor?.addEventListener("enhancedload", () => apply(current()));
    });
})();

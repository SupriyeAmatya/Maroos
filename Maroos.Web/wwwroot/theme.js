// =====================================================================================================
// FILE: Maroos.Web/wwwroot/theme.js
// PURPOSE: Light and dark mode ("Midnight plum"). Decides the theme, applies it, and makes the top bar's
// moon/sun button work. Like nav.js, it runs in the browser because the layout isn't interactive.
// HOW: Bootstrap 5.3 reads data-bs-theme="light" | "dark" on <html> and switches its own colors;
// app.css and NavMenu.razor.css swap the site's colors under [data-bs-theme="dark"].
// WHICH THEME: a choice saved with the button (localStorage "maroos.theme") wins; with no saved choice,
// the device's setting (the prefers-color-scheme media query) decides, and is followed live.
// WHERE: App.razor loads it in <head> before any stylesheet, so the first paint is already the right theme.
// =====================================================================================================
// Light and dark mode. The page's theme is Bootstrap's data-bs-theme attribute on <html> ("light" or "dark").
// A choice made with the top bar's toggle is kept in this browser's localStorage; without one, the page follows the
// device's setting and keeps following it if that changes. This file loads in <head> before the stylesheets, so a
// dark page is dark from the first paint.
// (An immediately-invoked function keeps these names out of the global scope.)
(() => {
    // The localStorage key for the saved choice.
    const key = "maroos.theme";
    // deviceDark: a live query for "does the device prefer dark?". The ?. calls mean an old browser without
    // matchMedia simply gets light mode instead of an error.
    const deviceDark = window.matchMedia?.("(prefers-color-scheme: dark)");

    // saved: the saved choice, "dark" or "light", or null if there is none (or storage can't be read).
    const saved = () => {
        try {
            const value = localStorage.getItem(key);
            return value === "dark" || value === "light" ? value : null;
        } catch {
            return null; // storage blocked: follow the device
        }
    };

    // save: remember the choice (quietly does nothing if storage is blocked).
    const save = theme => {
        try {
            localStorage.setItem(key, theme);
        } catch {
            // Can't remember it, but the click still applies for this page.
        }
    };

    // current: the theme to show now: the saved choice, else the device's setting.
    const current = () => saved() ?? (deviceDark?.matches ? "dark" : "light");

    // apply: show a theme: the attribute on <html>, and aria-pressed on the toggle so screen readers announce
    // whether dark mode is on.
    const apply = theme => {
        document.documentElement.setAttribute("data-bs-theme", theme);
        document.querySelectorAll("[data-theme-toggle]").forEach(button => button.setAttribute("aria-pressed", String(theme === "dark")));
    };

    // Straight away, while <head> is still loading: no flash of the wrong theme.
    apply(current());

    // No choice saved yet: follow the device when its setting changes.
    deviceDark?.addEventListener?.("change", () => {
        if (!saved())
            apply(current());
    });

    // One click listener for the whole page: if the click was on (or inside) the toggle, switch to the other
    // theme and save it. From then on the saved choice wins over the device setting.
    document.addEventListener("click", event => {
        if (!event.target.closest?.("[data-theme-toggle]"))
            return;
        const next = document.documentElement.getAttribute("data-bs-theme") === "dark" ? "light" : "dark";
        save(next);
        apply(next);
    });

    // Once the page has loaded: update the button (it didn't exist yet above), and re-apply after every
    // enhanced navigation.
    document.addEventListener("DOMContentLoaded", () => {
        apply(current());
        // Blazor's enhanced navigation swaps pages without a full load and resets the server-rendered markup.
        window.Blazor?.addEventListener("enhancedload", () => apply(current()));
    });
})();

// =====================================================================================================
// FILE: Maroos.Web/wwwroot/nav.js
// PURPOSE: Makes the top bar's pin button work. The bar is rendered on the server without Blazor
// interactivity, so this small script does the clicking and remembering in the browser.
// HOW: the choice is saved in localStorage ("maroos.navPinned" = "1" or "0") and shown by adding the
// .nav-pinned class to <html>; NavMenu.razor.css shows every label when that class is present.
// =====================================================================================================
// The top bar's "Always show labels" pin. The choice lives in this browser's localStorage and is applied as the
// .nav-pinned class on <html>. This file loads in <head>, so a pinned bar is drawn pinned from the start.
// (An immediately-invoked function keeps these names out of the global scope.)
(() => {
    // The localStorage key for the choice.
    const key = "maroos.navPinned";

    // isPinned: the saved choice (false if nothing is saved or storage can't be read).
    const isPinned = () => {
        try {
            return localStorage.getItem(key) === "1";
        } catch {
            return false; // storage blocked (private mode, site data off): labels on hover only
        }
    };

    // setPinned: save the choice (quietly does nothing if storage is blocked).
    const setPinned = pinned => {
        try {
            localStorage.setItem(key, pinned ? "1" : "0");
        } catch {
            // Can't remember it, but the click still applies for this page.
        }
    };

    // apply: show the choice: the class on <html>, and aria-pressed on the pin button so screen readers
    // announce whether it's on.
    const apply = pinned => {
        document.documentElement.classList.toggle("nav-pinned", pinned);
        document.querySelectorAll("[data-nav-pin]").forEach(button => button.setAttribute("aria-pressed", String(pinned)));
    };

    // Straight away, while the page is still loading (so there's no flash of the wrong state).
    apply(isPinned());

    // One click listener for the whole page: if the click was on (or inside) the pin button, flip the choice.
    document.addEventListener("click", event => {
        if (!event.target.closest?.("[data-nav-pin]"))
            return;
        const pinned = !document.documentElement.classList.contains("nav-pinned");
        setPinned(pinned);
        apply(pinned);
    });

    // Once the page has loaded: update the button (it didn't exist yet above), and re-apply after every
    // enhanced navigation.
    document.addEventListener("DOMContentLoaded", () => {
        apply(isPinned());
        // Blazor's enhanced navigation swaps pages without a full load and resets the server-rendered markup.
        window.Blazor?.addEventListener("enhancedload", () => apply(isPinned()));
    });
})();

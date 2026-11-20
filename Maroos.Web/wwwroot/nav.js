// The top bar's "Always show labels" pin. The choice lives in this browser's localStorage and is applied as the
// .nav-pinned class on <html>. This file loads in <head>, so a pinned bar is drawn pinned from the start.
(() => {
    const key = "maroos.navPinned";

    const isPinned = () => {
        try {
            return localStorage.getItem(key) === "1";
        } catch {
            return false; // storage blocked (private mode, site data off): labels on hover only
        }
    };

    const setPinned = pinned => {
        try {
            localStorage.setItem(key, pinned ? "1" : "0");
        } catch {
            // Can't remember it, but the click still applies for this page.
        }
    };

    const apply = pinned => {
        document.documentElement.classList.toggle("nav-pinned", pinned);
        document.querySelectorAll("[data-nav-pin]").forEach(button => button.setAttribute("aria-pressed", String(pinned)));
    };

    apply(isPinned());

    document.addEventListener("click", event => {
        if (!event.target.closest?.("[data-nav-pin]"))
            return;
        const pinned = !document.documentElement.classList.contains("nav-pinned");
        setPinned(pinned);
        apply(pinned);
    });

    document.addEventListener("DOMContentLoaded", () => {
        apply(isPinned());
        // Blazor's enhanced navigation swaps pages without a full load and resets the server-rendered markup.
        window.Blazor?.addEventListener("enhancedload", () => apply(isPinned()));
    });
})();

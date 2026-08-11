(() => {
    "use strict";

    const form = document.querySelector("[data-demo-reset-form]");
    if (!form) {
        return;
    }

    const submitButton = form.querySelector("[data-demo-reset-submit]");
    const overlay = document.querySelector("[data-demo-reset-overlay]");
    const errorPanel = form.querySelector("[data-demo-reset-error]");
    const correlation = form.querySelector("[data-demo-reset-correlation]");
    let submitting = false;

    const restore = (correlationId) => {
        submitting = false;
        form.setAttribute("aria-busy", "false");
        if (submitButton) {
            submitButton.disabled = false;
        }
        overlay?.classList.add("d-none");
        overlay?.classList.remove("d-flex");
        if (correlation) {
            correlation.textContent = correlationId || "不明";
        }
        errorPanel?.classList.remove("d-none");
        errorPanel?.focus();
    };

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (submitting) {
            return;
        }

        submitting = true;
        form.setAttribute("aria-busy", "true");
        errorPanel?.classList.add("d-none");
        if (submitButton) {
            submitButton.disabled = true;
        }
        overlay?.classList.remove("d-none");
        overlay?.classList.add("d-flex");

        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (response.ok) {
                window.location.assign("/");
                return;
            }

            restore(response.headers.get("X-Correlation-ID"));
        } catch {
            restore(null);
        }
    });
})();

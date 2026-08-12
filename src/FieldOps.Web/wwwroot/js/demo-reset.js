(() => {
    "use strict";

    const form = document.querySelector("[data-demo-reset-form]");
    if (!form) {
        return;
    }

    const submitButton = form.querySelector("[data-demo-reset-submit]");
    const overlay = document.querySelector("[data-demo-reset-overlay]");
    const errorPanel = form.querySelector("[data-demo-reset-error]");
    const guidance = form.querySelector("[data-demo-reset-guidance]");
    const correlation = form.querySelector("[data-demo-reset-correlation]");
    let submitting = false;

    const restore = (correlationId, messages) => {
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
        if (guidance) {
            guidance.textContent = messages?.length
                ? messages.join(" ")
                : "初期化に失敗しました。入力内容を確認して再試行してください。";
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
                const result = await response.json();
                window.location.assign(result.redirectUrl || "/");
                return;
            }

            let problem = null;
            if (response.headers.get("Content-Type")?.includes("application/json")) {
                problem = await response.json();
            }
            const messages = problem?.errors
                ? Object.values(problem.errors).flatMap(value => Array.isArray(value) ? value : [])
                : [];
            if (problem?.retry) {
                messages.push(problem.retry);
            }
            restore(problem?.correlationId || response.headers.get("X-Correlation-ID"), messages);
        } catch {
            restore(null, []);
        }
    });
})();

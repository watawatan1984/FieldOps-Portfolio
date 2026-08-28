// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

const validationSummary = document.querySelector('.validation-summary-errors[tabindex="-1"]');
if (validationSummary) {
  validationSummary.focus();
}

if (document.querySelector('[data-remove-query-after-load]')) {
  window.history.replaceState(null, document.title, window.location.pathname);
}

document.querySelectorAll('[data-history-back]').forEach(link => {
  link.addEventListener('click', event => {
    if (window.history.length <= 1) {
      return;
    }

    event.preventDefault();
    window.history.back();
  });
});

const primaryNavigation = document.querySelector('#primaryNavigation');
const primaryNavigationToggle = document.querySelector('[aria-controls="primaryNavigation"]');
if (primaryNavigation && primaryNavigationToggle) {
  primaryNavigation.addEventListener('shown.bs.offcanvas', () => {
    primaryNavigationToggle.setAttribute('aria-expanded', 'true');
    primaryNavigation.querySelector('[data-bs-dismiss="offcanvas"]')?.focus();
  });
  primaryNavigation.addEventListener('hidden.bs.offcanvas', () => {
    primaryNavigationToggle.setAttribute('aria-expanded', 'false');
    primaryNavigationToggle.focus();
  });
}

const confirmActionModal = document.querySelector('[data-confirm-action-modal]');
if (confirmActionModal && window.bootstrap) {
  const modal = new bootstrap.Modal(confirmActionModal);
  const title = confirmActionModal.querySelector('[data-confirm-modal-title]');
  const target = confirmActionModal.querySelector('[data-confirm-modal-target]');
  const message = confirmActionModal.querySelector('[data-confirm-modal-message]');
  const impact = confirmActionModal.querySelector('[data-confirm-modal-impact]');
  const runButton = confirmActionModal.querySelector('[data-confirm-run]');
  let pendingForm = null;
  let pendingSubmitter = null;
  let confirmedForm = null;

  document.addEventListener('submit', event => {
    if (confirmedForm === event.target) {
      confirmedForm = null;
      return;
    }

    const submitter = event.submitter;
    if (!(submitter instanceof HTMLElement) || !submitter.matches('[data-confirm-action]')) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    pendingForm = event.target;
    pendingSubmitter = submitter;

    title.textContent = submitter.dataset.confirmTitle || '実行しますか';
    target.textContent = submitter.dataset.confirmTarget || submitter.textContent.trim() || '選択した情報';
    message.textContent = submitter.dataset.confirmMessage || 'この操作を実行します。';
    impact.textContent = submitter.dataset.confirmImpact || '実行後、画面の内容が更新されます。';
    modal.show();
  }, true);

  runButton?.addEventListener('click', () => {
    if (!pendingForm) {
      return;
    }

    const form = pendingForm;
    const submitter = pendingSubmitter;
    confirmedForm = form;
    pendingForm = null;
    pendingSubmitter = null;
    modal.hide();
    form.requestSubmit(submitter);
  });

  confirmActionModal.addEventListener('hidden.bs.modal', () => {
    if (pendingSubmitter) {
      pendingSubmitter.focus();
    }

    pendingForm = null;
    pendingSubmitter = null;
  });
}

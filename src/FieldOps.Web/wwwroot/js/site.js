// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

const validationSummary = document.querySelector('.validation-summary-errors[tabindex="-1"]');
if (validationSummary) {
  validationSummary.focus();
}

if (document.querySelector('[data-remove-query-after-load]')) {
  window.history.replaceState(null, document.title, window.location.pathname);
}

const primaryNavigation = document.querySelector('#primaryNavigation');
const primaryNavigationToggle = document.querySelector('[aria-controls="primaryNavigation"]');
if (primaryNavigation && primaryNavigationToggle) {
  primaryNavigation.addEventListener('shown.bs.offcanvas', () => {
    primaryNavigationToggle.setAttribute('aria-expanded', 'true');
    primaryNavigation.querySelector('[data-bs-dismiss="offcanvas"]')?.focus();
  });
  primaryNavigation.addEventListener('hidden.bs.offcanvas', () => {
    primaryNavigationToggle.setAttribute('aria-expanded', 'false');
  });
}

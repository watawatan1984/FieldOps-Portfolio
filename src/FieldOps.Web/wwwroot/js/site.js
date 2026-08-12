// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

const validationSummary = document.querySelector('.validation-summary-errors[tabindex="-1"]');
if (validationSummary) {
  validationSummary.focus();
}

if (document.querySelector('[data-remove-query-after-load]')) {
  window.history.replaceState(null, document.title, window.location.pathname);
}

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
  let restoreSubmitter = null;

  const restoreFocusToSubmitter = () => {
    if (!restoreSubmitter) {
      return;
    }

    const target = restoreSubmitter;
    [0, 50, 150, 300].forEach(delay => {
      window.setTimeout(() => {
        if (document.body.contains(target)) {
          target.focus({ preventScroll: true });
        }
      }, delay);
    });
    window.setTimeout(() => {
      if (restoreSubmitter === target) {
        restoreSubmitter = null;
      }
    }, 350);
  };

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
    restoreSubmitter = submitter;

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
    restoreSubmitter = null;
    modal.hide();
    form.requestSubmit(submitter);
  });

  confirmActionModal.querySelector('[data-confirm-cancel]')?.addEventListener('click', restoreFocusToSubmitter);

  confirmActionModal.addEventListener('hide.bs.modal', restoreFocusToSubmitter);

  confirmActionModal.addEventListener('hidden.bs.modal', () => {
    restoreFocusToSubmitter();

    pendingForm = null;
    pendingSubmitter = null;
  });
}

const quoteLineItemsContainer = document.querySelector('[data-line-items-container]');
if (quoteLineItemsContainer) {
  const addLineItemButton = document.querySelector('[data-add-line-item]');
  const taxRateInput = document.querySelector('[data-quote-tax-rate]');
  const subtotalOutput = document.querySelector('[data-quote-subtotal]');
  const taxOutput = document.querySelector('[data-quote-tax]');
  const totalOutput = document.querySelector('[data-quote-total]');
  const quoteCurrencyFormatter = new Intl.NumberFormat('ja-JP', { style: 'currency', currency: 'JPY', maximumFractionDigits: 0 });

  const formatQuoteCurrency = value => quoteCurrencyFormatter.format(Math.round(value));

  const reindexLineItemRows = () => {
    const rows = quoteLineItemsContainer.querySelectorAll('[data-line-item-row]');
    rows.forEach((row, index) => {
      row.querySelectorAll('[data-field]').forEach(field => {
        const fieldName = field.getAttribute('data-field');
        field.name = `LineItems[${index}].${fieldName}`;
        field.id = `LineItems_${index}__${fieldName}`;
      });
      row.querySelectorAll('[data-field-label]').forEach(label => {
        const fieldName = label.getAttribute('data-field-label');
        label.setAttribute('for', `LineItems_${index}__${fieldName}`);
      });
      const removeButton = row.querySelector('[data-remove-line-item]');
      if (removeButton) {
        removeButton.disabled = rows.length <= 1;
      }
    });
  };

  const createLineItemRow = () => {
    const row = document.createElement('div');
    row.className = 'row g-2 align-items-end mb-2 quote-line-item';
    row.setAttribute('data-line-item-row', '');
    row.innerHTML = [
      '<div class="col-md-4">',
      '  <label class="form-label" data-field-label="Description">品名</label>',
      '  <input class="form-control" maxlength="200" required data-field="Description" />',
      '  <span class="text-danger"></span>',
      '</div>',
      '<div class="col-md-2">',
      '  <label class="form-label" data-field-label="UnitName">単位</label>',
      '  <input class="form-control" maxlength="16" required data-field="UnitName" />',
      '  <span class="text-danger"></span>',
      '</div>',
      '<div class="col-md-2">',
      '  <label class="form-label" data-field-label="Quantity">数量</label>',
      '  <input class="form-control" type="number" step="0.01" min="0.01" data-field="Quantity" data-line-item-quantity />',
      '  <span class="text-danger"></span>',
      '</div>',
      '<div class="col-md-2">',
      '  <label class="form-label" data-field-label="UnitPrice">単価</label>',
      '  <input class="form-control" type="number" step="0.01" min="0" data-field="UnitPrice" data-line-item-unit-price />',
      '  <span class="text-danger"></span>',
      '</div>',
      '<div class="col-md-2">',
      '  <button type="button" class="btn btn-outline-danger w-100" data-remove-line-item>この行を削除</button>',
      '</div>'
    ].join('');
    return row;
  };

  const computeQuoteTotals = () => {
    let subtotal = 0;
    quoteLineItemsContainer.querySelectorAll('[data-line-item-row]').forEach(row => {
      const quantity = parseFloat(row.querySelector('[data-line-item-quantity]')?.value) || 0;
      const unitPrice = parseFloat(row.querySelector('[data-line-item-unit-price]')?.value) || 0;
      subtotal += Math.round(quantity * unitPrice * 100) / 100;
    });
    const taxRate = parseFloat(taxRateInput?.value) || 0;
    const tax = Math.floor(subtotal * taxRate / 100);
    const total = subtotal + tax;

    if (subtotalOutput) {
      subtotalOutput.textContent = formatQuoteCurrency(subtotal);
    }
    if (taxOutput) {
      taxOutput.textContent = formatQuoteCurrency(tax);
    }
    if (totalOutput) {
      totalOutput.textContent = formatQuoteCurrency(total);
    }
  };

  addLineItemButton?.addEventListener('click', () => {
    quoteLineItemsContainer.appendChild(createLineItemRow());
    reindexLineItemRows();
    computeQuoteTotals();
  });

  quoteLineItemsContainer.addEventListener('click', event => {
    const removeButton = event.target.closest('[data-remove-line-item]');
    if (!removeButton) {
      return;
    }

    const row = removeButton.closest('[data-line-item-row]');
    if (!row || quoteLineItemsContainer.querySelectorAll('[data-line-item-row]').length <= 1) {
      return;
    }

    row.remove();
    reindexLineItemRows();
    computeQuoteTotals();
  });

  quoteLineItemsContainer.addEventListener('input', event => {
    if (event.target.matches('[data-line-item-quantity], [data-line-item-unit-price]')) {
      computeQuoteTotals();
    }
  });

  taxRateInput?.addEventListener('input', computeQuoteTotals);

  reindexLineItemRows();
  computeQuoteTotals();
}

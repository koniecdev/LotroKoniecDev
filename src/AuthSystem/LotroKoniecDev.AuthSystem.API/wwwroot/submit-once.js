// A form marked data-submit-once is sent once per page view, and its button shows that the work is in
// progress. Most of these forms carry a one-time link. A second click would reach the server after the
// first one used the link up, and the browser would show that second answer: "link dead" (#871).
// It lives in a file because the auth CSP sends script-src 'self', which blocks an inline script (#670, #693).
// The button is disabled inside the submit event, so the browser leaves it out of the posted data.
// Do not give the submit button of such a form a name: its value would never reach the server.
(function () {
    var forms = document.querySelectorAll('form[data-submit-once]');
    Array.prototype.forEach.call(forms, function (form) {
        var btn = form.querySelector('button[type="submit"]');
        var label = btn ? btn.querySelector('.submit-btn-label') : null;
        var idleText = label ? label.textContent : null;
        var sent = false;

        // Firefox can bring back a disabled state that a script set, when the page is reloaded. The server
        // never sends a disabled button here, so a disabled one at start is always left over.
        if (btn) { btn.disabled = false; }

        form.addEventListener('submit', function (event) {
            if (sent) {
                event.preventDefault();
                return;
            }
            sent = true;
            if (!btn) { return; }
            btn.disabled = true;
            btn.classList.add('is-loading');
            if (label && btn.dataset.busyLabel) { label.textContent = btn.dataset.busyLabel; }
        });

        // The back button can restore this page from the browser's cache with the button still disabled,
        // although no request is running any more.
        window.addEventListener('pageshow', function (event) {
            if (!event.persisted) { return; }
            // The link this form sent may be used up by now, and only the server knows whether this browser
            // used it. A fresh GET lets the page show "done" instead of a button that calls the link dead
            // (#941). replace, not reload: when this page answered a POST, a reload would send it again.
            if (sent && form.hasAttribute('data-recheck-when-restored')) {
                window.location.replace(window.location.href);
                return;
            }
            sent = false;
            if (!btn) { return; }
            btn.disabled = false;
            btn.classList.remove('is-loading');
            if (label) { label.textContent = idleText; }
        });
    });
})();

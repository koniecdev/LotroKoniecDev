// The password toggle and the busy state of the login button. It lives in a file because the
// auth CSP sends script-src 'self', which blocks an inline script (#670, #693).
(function () {
    var toggle = document.getElementById('password-toggle');
    var icon = document.getElementById('password-toggle-icon');
    var pwd = document.getElementById('password-input');
    var eyeOpen = '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>';
    var eyeOff = '<path d="M3 3l18 18"/><path d="M10.6 6.1A9.7 9.7 0 0 1 12 6c6.5 0 10 6 10 6a17 17 0 0 1-3 3.7"/><path d="M6.2 6.2C3.7 7.9 2 12 2 12s3.5 6 10 6c1.8 0 3.4-.4 4.8-1.1"/><path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/>';
    if (toggle && pwd && icon) {
        toggle.addEventListener('click', function () {
            var shown = pwd.getAttribute('type') === 'text';
            pwd.setAttribute('type', shown ? 'password' : 'text');
            toggle.setAttribute('aria-pressed', shown ? 'false' : 'true');
            toggle.setAttribute('aria-label', shown ? 'Pokaż hasło' : 'Ukryj hasło');
            icon.innerHTML = shown ? eyeOpen : eyeOff;
        });
    }

    var form = document.getElementById('login-form');
    var btn = document.getElementById('submit-btn');
    if (form && btn) {
        form.addEventListener('submit', function () {
            if (btn.disabled) { return; }
            btn.disabled = true;
            btn.classList.add('is-loading');
            var label = btn.querySelector('.submit-btn-label');
            if (label) { label.textContent = 'Logowanie…'; }
        });
    }
})();

// Language toggle (remembered per browser) and copy buttons for the install commands.
(function () {
  var root = document.documentElement;
  var buttons = document.querySelectorAll('[data-set-lang]');

  function apply(lang) {
    root.setAttribute('data-lang', lang);
    root.setAttribute('lang', lang);
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].setAttribute('aria-pressed', buttons[i].getAttribute('data-set-lang') === lang ? 'true' : 'false');
    }
  }

  for (var i = 0; i < buttons.length; i++) {
    buttons[i].addEventListener('click', function () {
      var lang = this.getAttribute('data-set-lang');
      apply(lang);
      try { localStorage.setItem('tbc-lang', lang); } catch (e) {}
    });
  }
  apply(root.getAttribute('data-lang') === 'es' ? 'es' : 'en');

  var copies = document.querySelectorAll('[data-copy]');
  for (var j = 0; j < copies.length; j++) {
    copies[j].addEventListener('click', function () {
      var button = this;
      var label = button.textContent;
      // Copy only the commands, not the comment lines or the button label.
      var lines = button.parentNode.innerText.split('\n').filter(function (line) {
        line = line.trim();
        return line && line.charAt(0) !== '#' && line !== label;
      });
      var done = function () {
        button.textContent = root.getAttribute('data-lang') === 'es' ? '¡Copiado!' : 'Copied!';
        setTimeout(function () { button.textContent = label; }, 1500);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(lines.join('\n')).then(done, function () {});
      }
    });
  }
})();

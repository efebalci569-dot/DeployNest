// Applies the saved theme before first paint to avoid a light/dark flash.
(function () {
  var theme = null;
  try { theme = localStorage.getItem('dn-theme'); } catch (e) { /* storage may be blocked */ }
  if (theme === 'light' || theme === 'dark') {
    document.documentElement.setAttribute('data-theme', theme);
  }
})();

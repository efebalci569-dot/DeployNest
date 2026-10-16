// Applies the saved theme before first paint to avoid a light/dark flash, and marks that
// scripts run so scroll-reveal styles only hide content when they can also show it again.
(function () {
  var root = document.documentElement;
  root.classList.add('js');
  var theme = null;
  try { theme = localStorage.getItem('dn-theme'); } catch (e) { /* storage may be blocked */ }
  if (theme === 'light' || theme === 'dark') {
    root.setAttribute('data-theme', theme);
  }
})();

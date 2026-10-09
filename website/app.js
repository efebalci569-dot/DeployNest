// DeployNest website behaviour: theme, navigation, program catalog and release lookup.
(function () {
  'use strict';

  var REPO = 'efebalci569-dot/DeployNest';
  var RELEASES_URL = 'https://github.com/' + REPO + '/releases';
  var BUILD_DOCS_URL = 'https://github.com/' + REPO + '#kaynak-kodunu-derleme';
  var root = document.documentElement;

  // ---- Theme -------------------------------------------------------------
  var darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

  function isDark() {
    var forced = root.getAttribute('data-theme');
    return forced ? forced === 'dark' : darkQuery.matches;
  }

  function syncThemedImages() {
    var dark = isDark();
    document.querySelectorAll('img.screenshot-themed').forEach(function (img) {
      var src = dark ? img.getAttribute('data-dark') : img.getAttribute('data-light');
      if (src && img.getAttribute('src') !== src) img.setAttribute('src', src);
    });
  }

  document.getElementById('theme-toggle').addEventListener('click', function () {
    var next = isDark() ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('dn-theme', next); } catch (e) { /* storage may be blocked */ }
    syncThemedImages();
  });
  darkQuery.addEventListener('change', syncThemedImages);
  syncThemedImages();

  // ---- Mobile menu -------------------------------------------------------
  var nav = document.getElementById('site-nav');
  var menuButton = document.getElementById('menu-toggle');
  menuButton.addEventListener('click', function () {
    var open = nav.classList.toggle('open');
    menuButton.setAttribute('aria-expanded', String(open));
    menuButton.setAttribute('aria-label', open ? 'Menüyü kapat' : 'Menüyü aç');
  });
  nav.addEventListener('click', function (e) {
    if (e.target.closest('a')) {
      nav.classList.remove('open');
      menuButton.setAttribute('aria-expanded', 'false');
    }
  });

  // ---- Screenshot lightbox ----------------------------------------------
  var lightbox = document.getElementById('lightbox');
  document.querySelectorAll('.gallery-item').forEach(function (button) {
    button.addEventListener('click', function () {
      var img = lightbox.querySelector('img');
      img.src = button.getAttribute('data-full');
      img.alt = button.querySelector('img').alt;
      if (typeof lightbox.showModal === 'function') lightbox.showModal();
      else window.open(button.getAttribute('data-full'), '_blank', 'noopener');
    });
  });
  lightbox.addEventListener('click', function (e) {
    if (e.target === lightbox) lightbox.close();
  });

  // ---- Catalog -----------------------------------------------------------
  // Same tolerant matching as the app: case-, Turkish-character- and diacritic-insensitive.
  function normalize(text) {
    return (text || '')
      .replace(/[İIı]/g, 'i')
      .normalize('NFD')
      .replace(/[̀-ͯ]/g, '')
      .toLowerCase()
      .replace(/\s+/g, ' ')
      .trim();
  }

  function compact(text) {
    return normalize(text).replace(/[^\p{L}\p{N}]/gu, '');
  }

  function tr(value) {
    if (!value) return '';
    return value.tr || value[Object.keys(value)[0]] || '';
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  var catalogRoot = document.getElementById('catalog');
  var emptyMessage = document.getElementById('catalog-empty');
  var searchInput = document.getElementById('catalog-search');
  var chipsRoot = document.getElementById('catalog-categories');
  var state = { catalog: null, category: null, query: '' };

  function matches(program, categoryName) {
    var tokens = normalize(state.query).split(' ').filter(Boolean);
    if (!tokens.length) return true;
    var haystack = normalize([program.name, program.publisher, tr(program.summary), categoryName].concat(program.tags || []).join(' '));
    var haystackCompact = compact(haystack);
    return tokens.every(function (token) {
      var c = compact(token);
      return haystack.indexOf(token) >= 0 || (c && haystackCompact.indexOf(c) >= 0);
    });
  }

  function renderPrograms() {
    var catalog = state.catalog;
    catalogRoot.textContent = '';
    var shown = 0;

    catalog.categories.forEach(function (category) {
      if (state.category && state.category !== category.id) return;
      var categoryName = tr(category.name);
      var programs = catalog.software.filter(function (p) {
        return p.category === category.id && matches(p, categoryName);
      });
      if (!programs.length) return;
      shown += programs.length;

      var block = el('section', 'category-block');
      var title = el('h3', 'category-title', categoryName);
      title.appendChild(el('span', null, programs.length + ' program'));
      block.appendChild(title);

      var grid = el('div', 'program-grid');
      programs.forEach(function (p) {
        var card = el('article', 'program');
        var logo = el('div', 'program-logo');
        var img = document.createElement('img');
        img.src = 'assets/apps/' + encodeURIComponent(p.id) + '.png';
        img.alt = p.name + ' logosu';
        img.loading = 'lazy';
        img.width = 34;
        img.height = 34;
        logo.appendChild(img);
        card.appendChild(logo);

        var body = el('div', 'program-body');
        body.appendChild(el('div', 'program-name', p.name));
        body.appendChild(el('div', 'program-publisher', p.publisher));
        body.appendChild(el('p', 'program-summary', tr(p.summary)));
        var meta = el('div', 'program-meta');
        if (p.winget) {
          var winget = el('span', 'tag tag-accent', 'winget');
          winget.title = 'winget paket kimliği: ' + p.winget.id;
          meta.appendChild(winget);
        }
        if (p.direct) {
          var direct = el('span', 'tag', 'winget olmadan da');
          direct.title = 'winget yoksa yayıncının resmi adresinden indirilip SHA-256/imza ile doğrulanarak kurulabilir';
          meta.appendChild(direct);
        }
        if (p.license) meta.appendChild(el('span', 'tag', tr(p.license)));
        body.appendChild(meta);
        card.appendChild(body);
        grid.appendChild(card);
      });
      block.appendChild(grid);
      catalogRoot.appendChild(block);
    });

    emptyMessage.hidden = shown !== 0;
  }

  function renderChips() {
    chipsRoot.textContent = '';
    var all = [{ id: null, name: 'Tümü' }].concat(state.catalog.categories.map(function (c) {
      return { id: c.id, name: tr(c.name) };
    }));
    all.forEach(function (c) {
      var chip = el('button', 'chip', c.name);
      chip.type = 'button';
      chip.setAttribute('aria-pressed', String(state.category === c.id));
      chip.addEventListener('click', function () {
        state.category = c.id;
        renderChips();
        renderPrograms();
      });
      chipsRoot.appendChild(chip);
    });
  }

  searchInput.addEventListener('input', function () {
    state.query = searchInput.value;
    if (state.catalog) renderPrograms();
  });

  fetch('data/catalog.json')
    .then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    })
    .then(function (catalog) {
      state.catalog = catalog;
      document.querySelectorAll('[data-count-programs]').forEach(function (n) { n.textContent = catalog.software.length; });
      document.querySelectorAll('[data-count-categories]').forEach(function (n) { n.textContent = catalog.categories.length; });
      renderChips();
      renderPrograms();
    })
    .catch(function () {
      catalogRoot.textContent = '';
      var p = el('p', 'catalog-empty', 'Program listesi yüklenemedi. ');
      var link = el('a', null, 'Katalog dosyasını aç');
      link.href = 'data/catalog.json';
      p.appendChild(link);
      catalogRoot.appendChild(p);
    });

  // ---- Release lookup ----------------------------------------------------
  // The download button only points at a real published release asset; it never invents a link.
  function setDownload(href, label, meta, disabled) {
    document.querySelectorAll('[data-download]').forEach(function (a) {
      a.href = href;
      if (disabled) a.setAttribute('aria-disabled', 'true');
      else a.removeAttribute('aria-disabled');
    });
    document.querySelectorAll('[data-download-label]').forEach(function (s) { s.textContent = label; });
    if (meta) document.querySelectorAll('[data-release-meta]').forEach(function (m) { m.textContent = meta; });
  }

  function formatSize(bytes) {
    return (bytes / 1048576).toLocaleString('tr-TR', { maximumFractionDigits: 1 }) + ' MB';
  }

  fetch('https://api.github.com/repos/' + REPO + '/releases/latest', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) {
      if (r.status === 404) {
        setDownload(BUILD_DOCS_URL, 'Henüz yayımlanmış sürüm yok',
          'İlk sürüm yayımlandığında bu buton doğrudan EXE dosyasına bağlanır. Şimdilik uygulamayı kaynak koddan derleyebilirsin.', true);
        return null;
      }
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    })
    .then(function (release) {
      if (!release) return;
      var assets = release.assets || [];
      var asset = assets.find(function (a) { return /\.exe$/i.test(a.name); }) ||
                  assets.find(function (a) { return /win-x64.*\.zip$/i.test(a.name); });
      var date = new Date(release.published_at).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' });
      if (asset) {
        setDownload(asset.browser_download_url, 'İndir (' + release.tag_name + ')',
          release.tag_name + ' · ' + asset.name + ' · ' + formatSize(asset.size) + ' · ' + date, false);
      } else {
        setDownload(release.html_url, 'Sürüm sayfasını aç (' + release.tag_name + ')', release.tag_name + ' · ' + date, false);
      }
    })
    .catch(function () {
      setDownload(RELEASES_URL, 'Sürümler sayfasına git', 'Sürüm bilgisi şu anda alınamadı; indirmeler GitHub Releases sayfasında listelenir.', false);
    });
})();

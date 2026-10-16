// DeployNest website behaviour: theme, navigation, install-queue demo, program catalog and release lookup.
(function () {
  'use strict';

  var REPO = 'efebalci569-dot/DeployNest';
  var RELEASES_URL = 'https://github.com/' + REPO + '/releases';
  var BUILD_DOCS_URL = 'https://github.com/' + REPO + '#kaynak-kodunu-derleme';
  var root = document.documentElement;
  var reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

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
      var thumb = button.querySelector('img');
      // Theme-aware screenshots open in the variant that is currently shown.
      var full = thumb.classList.contains('screenshot-themed') ? thumb.getAttribute('src') : button.getAttribute('data-full');
      img.src = full;
      img.alt = thumb.alt;
      if (typeof lightbox.showModal === 'function') lightbox.showModal();
      else window.open(full, '_blank', 'noopener');
    });
  });
  lightbox.addEventListener('click', function (e) {
    if (e.target === lightbox) lightbox.close();
  });

  // ---- Header border once the page scrolls -------------------------------
  var header = document.querySelector('.site-header');
  function syncHeader() { header.classList.toggle('is-scrolled', window.scrollY > 8); }
  window.addEventListener('scroll', syncHeader, { passive: true });
  syncHeader();

  // ---- Screenshot tabs (Windows / Linux) ----------------------------------
  var shotTabs = Array.prototype.slice.call(document.querySelectorAll('[data-shots]'));
  function selectShots(tab, focus) {
    shotTabs.forEach(function (t) {
      var selected = t === tab;
      t.setAttribute('aria-selected', String(selected));
      t.tabIndex = selected ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !selected;
    });
    if (focus) tab.focus();
  }
  shotTabs.forEach(function (tab, index) {
    tab.addEventListener('click', function () { selectShots(tab, false); });
    tab.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
      e.preventDefault();
      var next = shotTabs[(index + (e.key === 'ArrowRight' ? 1 : shotTabs.length - 1)) % shotTabs.length];
      selectShots(next, true);
    });
  });

  // ---- Reveal on scroll --------------------------------------------------
  var revealables = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window && !reducedMotion.matches) {
    var revealObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-visible');
        revealObserver.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.1 });
    revealables.forEach(function (node) { revealObserver.observe(node); });
  } else {
    revealables.forEach(function (node) { node.classList.add('is-visible'); });
  }

  // ---- Install queue demo ------------------------------------------------
  // Plays a pretend install session with real package ids from the catalog. The static
  // markup already shows a sensible mid-session state for no-JS and reduced motion.
  (function () {
    var demo = document.getElementById('queue-demo');
    if (!demo) return;
    var rows = Array.prototype.slice.call(demo.querySelectorAll('.q-row'));
    var command = document.getElementById('queue-command');
    var summary = document.getElementById('queue-summary');
    var platform = 'windows';
    var STATE_TEXT = { waiting: 'Bekliyor', downloading: 'İndiriliyor', verifying: 'Doğrulanıyor', installing: 'Kuruluyor', done: 'Kuruldu' };

    function source(row) {
      if (platform === 'windows') return { label: 'winget · ' + row.dataset.winget, cmd: 'winget install --id ' + row.dataset.winget + ' --exact --source winget --silent' };
      var parts = row.dataset.linux.split(' ');
      if (parts[0] === 'flathub') return { label: 'Flathub · ' + parts[1], cmd: 'flatpak install --user --noninteractive flathub ' + parts[1] };
      return { label: 'apt · ' + parts[1], cmd: 'apt-get install -y ' + parts[1] };
    }

    function setState(row, state, progress) {
      row.dataset.state = state;
      row.querySelector('.q-state').textContent = STATE_TEXT[state];
      var fill = row.querySelector('.q-fill');
      if (progress === undefined) fill.style.removeProperty('transform');
      else fill.style.transform = 'scaleX(' + progress + ')';
    }

    function refreshSources() {
      rows.forEach(function (row) { row.querySelector('.q-src').textContent = source(row).label; });
    }

    function refreshSummary() {
      var done = rows.filter(function (row) { return row.dataset.state === 'done'; }).length;
      summary.textContent = done + ' / ' + rows.length + ' kuruldu';
    }

    function activeRow() {
      return rows.find(function (row) { return row.dataset.state !== 'done' && row.dataset.state !== 'waiting'; }) ||
             rows.find(function (row) { return row.dataset.state === 'waiting'; });
    }

    demo.querySelectorAll('[data-demo-platform]').forEach(function (button) {
      button.addEventListener('click', function () {
        platform = button.getAttribute('data-demo-platform');
        demo.querySelectorAll('[data-demo-platform]').forEach(function (b) { b.setAttribute('aria-pressed', String(b === button)); });
        refreshSources();
        var row = activeRow();
        if (row) command.textContent = source(row).cmd;
      });
    });
    refreshSources();
    if (reducedMotion.matches) return;

    var timer = null;
    var running = false;
    function wait(ms, next) { timer = window.setTimeout(next, ms); }

    function installAt(index) {
      if (index >= rows.length) {
        wait(2600, function () {
          rows.forEach(function (row) { setState(row, 'waiting'); });
          refreshSummary();
          wait(700, function () { installAt(0); });
        });
        return;
      }
      var row = rows[index];
      command.textContent = source(row).cmd;
      var progress = 0;
      setState(row, 'downloading', 0);
      (function tick() {
        progress = Math.min(1, progress + 0.07 + Math.random() * 0.09);
        setState(row, 'downloading', progress);
        if (progress < 1) { wait(110, tick); return; }
        setState(row, 'verifying');
        wait(650, function () {
          setState(row, 'installing');
          wait(800, function () {
            setState(row, 'done');
            refreshSummary();
            wait(250, function () { installAt(index + 1); });
          });
        });
      })();
    }

    function start() {
      if (running) return;
      running = true;
      var first = rows.findIndex(function (row) { return row.dataset.state !== 'done'; });
      installAt(first < 0 ? rows.length : first);
    }
    function stop() {
      running = false;
      window.clearTimeout(timer);
    }

    // Only animate while the demo is on screen.
    if ('IntersectionObserver' in window) {
      new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) { if (entry.isIntersecting) start(); else stop(); });
      }).observe(demo);
    } else {
      start();
    }
  })();

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
  var platformRoot = document.getElementById('catalog-platforms');
  var state = { catalog: null, category: null, platform: '', query: '' };

  // Same rules as the apps: Windows needs a winget or direct source, Linux a "linux" section.
  function onWindows(p) { return !!(p.winget || p.direct); }
  function onLinux(p) { return !!p.linux; }
  function onPlatform(p) {
    if (state.platform === 'windows') return onWindows(p);
    if (state.platform === 'linux') return onLinux(p);
    return true;
  }

  // Short card label: Flathub works on every distribution; otherwise name the package managers.
  function linuxLabel(linux) {
    if (linux.flatpak) return 'Flathub';
    return ['apt', 'dnf', 'pacman'].filter(function (manager) {
      return linux[manager] && linux[manager].length;
    }).join('/');
  }

  function linuxTitle(linux) {
    var lines = [];
    if (linux.flatpak) lines.push('Flathub: ' + linux.flatpak);
    ['apt', 'dnf', 'pacman'].forEach(function (manager) {
      if (linux[manager] && linux[manager].length) lines.push(manager + ': ' + linux[manager].join(', '));
    });
    return lines.join('\n');
  }

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

  function platformTag(label, title) {
    var tag = el('span', 'tag tag-accent', label);
    tag.title = title;
    return tag;
  }

  function renderPrograms() {
    var catalog = state.catalog;
    catalogRoot.textContent = '';
    var shown = 0;

    catalog.categories.forEach(function (category) {
      if (state.category && state.category !== category.id) return;
      var categoryName = tr(category.name);
      var programs = catalog.software.filter(function (p) {
        return p.category === category.id && onPlatform(p) && matches(p, categoryName);
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
        if (onWindows(p)) {
          var windowsTitle = [];
          if (p.winget) windowsTitle.push('winget paket kimliği: ' + p.winget.id);
          if (p.direct) windowsTitle.push('winget yoksa yayıncının resmi adresinden indirilip SHA-256/imza ile doğrulanarak kurulabilir');
          meta.appendChild(platformTag(p.winget ? 'Windows · winget' : 'Windows', windowsTitle.join('\n')));
        }
        if (onLinux(p)) {
          meta.appendChild(platformTag('Linux · ' + linuxLabel(p.linux), linuxTitle(p.linux)));
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
    var categories = state.catalog.categories.filter(function (c) {
      return state.catalog.software.some(function (p) { return p.category === c.id && onPlatform(p); });
    });
    if (state.category && !categories.some(function (c) { return c.id === state.category; })) state.category = null;
    var all = [{ id: null, name: 'Tümü' }].concat(categories.map(function (c) {
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

  platformRoot.querySelectorAll('[data-platform]').forEach(function (button) {
    button.addEventListener('click', function () {
      state.platform = button.getAttribute('data-platform');
      platformRoot.querySelectorAll('[data-platform]').forEach(function (b) {
        b.setAttribute('aria-pressed', String(b === button));
      });
      if (!state.catalog) return;
      renderChips();
      renderPrograms();
    });
  });

  searchInput.addEventListener('input', function () {
    state.query = searchInput.value;
    if (state.catalog) renderPrograms();
  });

  // Two rows of real program logos, duplicated once so the CSS loop is seamless. Decorative:
  // the catalog below lists the same programs accessibly.
  function renderLogoWall(catalog) {
    var marquees = document.querySelectorAll('[data-marquee]');
    if (!marquees.length) return;
    // Programs from the ready-made groups, i.e. the ones most people look for first.
    var byId = {};
    catalog.software.forEach(function (p) { byId[p.id] = p; });
    var programs = [];
    (catalog.presets || []).forEach(function (preset) {
      (preset.software || []).forEach(function (id) {
        if (byId[id] && programs.indexOf(byId[id]) < 0) programs.push(byId[id]);
      });
    });
    if (programs.length < 24) programs = catalog.software.slice();
    programs = programs.slice(0, 64);
    // Interleave categories so look-alike logos (e.g. two .NET runtimes) do not sit side by side.
    var buckets = {};
    var order = [];
    programs.forEach(function (p) {
      if (!buckets[p.category]) { buckets[p.category] = []; order.push(p.category); }
      buckets[p.category].push(p);
    });
    programs = [];
    for (var round = 0; programs.length < 64; round++) {
      var added = false;
      order.forEach(function (category) {
        if (buckets[category][round]) { programs.push(buckets[category][round]); added = true; }
      });
      if (!added) break;
    }
    var half = Math.ceil(programs.length / 2);
    var groups = [programs.slice(0, half), programs.slice(half)];
    marquees.forEach(function (marquee, index) {
      var track = marquee.querySelector('.marquee-track');
      var group = groups[index] || [];
      track.textContent = '';
      for (var copy = 0; copy < 2; copy++) {
        group.forEach(function (p) {
          var tile = el('span', 'logo-tile');
          tile.title = p.name;
          var img = document.createElement('img');
          img.src = 'assets/apps/' + encodeURIComponent(p.id) + '.png';
          img.alt = '';
          img.width = 40;
          img.height = 40;
          img.decoding = 'async';
          tile.appendChild(img);
          track.appendChild(tile);
        });
      }
    });
  }

  function setCount(selector, value) {
    document.querySelectorAll(selector).forEach(function (n) { n.textContent = value; });
  }

  fetch('data/catalog.json')
    .then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    })
    .then(function (catalog) {
      state.catalog = catalog;
      setCount('[data-count-programs]', catalog.software.length);
      setCount('[data-count-windows]', catalog.software.filter(onWindows).length);
      setCount('[data-count-linux]', catalog.software.filter(onLinux).length);
      setCount('[data-count-categories]', catalog.categories.length);
      renderLogoWall(catalog);
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
  // Download buttons only point at real published release assets; they never invent a link.
  // Each platform uses the newest stable release that actually contains its package.
  var PACKAGES = {
    windows: [/\.exe$/i, /win-x64.*\.zip$/i],
    linux: [/linux-x64\.tar\.gz$/i],
    deb: [/_amd64\.deb$/i]
  };

  function setDownload(platform, href, label, disabled) {
    document.querySelectorAll('[data-download="' + platform + '"]').forEach(function (a) {
      a.href = href;
      if (disabled) a.setAttribute('aria-disabled', 'true');
      else a.removeAttribute('aria-disabled');
    });
    document.querySelectorAll('[data-download-label="' + platform + '"]').forEach(function (s) { s.textContent = label; });
  }

  function setMeta(platform, text) {
    document.querySelectorAll('[data-release-meta="' + platform + '"]').forEach(function (m) { m.textContent = text; });
  }

  function hideDownload(platform) {
    document.querySelectorAll('[data-download="' + platform + '"]').forEach(function (a) { a.hidden = true; });
  }

  function formatSize(bytes) {
    return (bytes / 1048576).toLocaleString('tr-TR', { maximumFractionDigits: 1 }) + ' MB';
  }

  function formatDate(iso) {
    return new Date(iso).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' });
  }

  function findPackage(releases, platform) {
    for (var i = 0; i < releases.length; i++) {
      var assets = releases[i].assets || [];
      for (var j = 0; j < PACKAGES[platform].length; j++) {
        var pattern = PACKAGES[platform][j];
        var asset = assets.find(function (a) { return pattern.test(a.name); });
        if (asset) return { release: releases[i], asset: asset };
      }
    }
    return null;
  }

  function describe(found) {
    return found.release.tag_name + ' · ' + found.asset.name + ' · ' + formatSize(found.asset.size) + ' · ' + formatDate(found.release.published_at);
  }

  fetch('https://api.github.com/repos/' + REPO + '/releases?per_page=20', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    })
    .then(function (list) {
      var releases = (list || []).filter(function (r) { return !r.draft && !r.prerelease; });
      var windows = findPackage(releases, 'windows');
      var linux = findPackage(releases, 'linux');
      var deb = findPackage(releases, 'deb');
      var latest = releases.find(function (r) { return (r.assets || []).length; });
      if (latest) document.querySelectorAll('[data-latest-tag]').forEach(function (n) { n.textContent = latest.tag_name; });

      if (windows) {
        setDownload('windows', windows.asset.browser_download_url, 'Windows için indir (' + windows.release.tag_name + ')', false);
        setMeta('windows', describe(windows));
      } else {
        setDownload('windows', BUILD_DOCS_URL, 'Henüz yayımlanmış sürüm yok', true);
        setMeta('windows', 'İlk sürüm yayımlandığında bu düğme doğrudan EXE dosyasına bağlanır. Şimdilik uygulamayı kaynak koddan derleyebilirsin.');
      }

      if (linux) {
        setDownload('linux', linux.asset.browser_download_url, 'Linux için indir (' + linux.release.tag_name + ')', false);
        setMeta('linux', describe(linux));
      } else {
        setDownload('linux', BUILD_DOCS_URL, 'Linux paketi henüz yayımlanmadı', true);
        setMeta('linux', 'Linux paketleri Linux destekli ilk sürümle birlikte burada yer alacak. Şimdilik uygulamayı kaynak koddan derleyebilirsin.');
      }

      if (deb) {
        setDownload('deb', deb.asset.browser_download_url, '.deb paketi (' + formatSize(deb.asset.size) + ')', false);
      } else {
        hideDownload('deb');
      }
    })
    .catch(function () {
      ['windows', 'linux'].forEach(function (platform) {
        setDownload(platform, RELEASES_URL, 'Sürümler sayfasına git', false);
        setMeta(platform, 'Sürüm bilgisi şu anda alınamadı; indirmeler GitHub Releases sayfasında listelenir.');
      });
      hideDownload('deb');
    });
})();

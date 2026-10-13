// Panel previews for the VCV Library icons on boxes: a module's link (.lib[data-library]) or a folded group's
// panels (.lib[data-panels], "plugin/model*count …").
// Shared: embedded in the static HTML export, and copied to the Blazor app's wwwroot/js at build time.
// Nothing is fetched until an icon is hovered; a loaded image is kept for the page's life (and the browser's HTTP cache
// keeps it longer), and a module the Library doesn't have is remembered, so it is never asked for again.
// Returns a handle whose dispose() removes the preview card and the listeners (the app calls it when the diagram goes away).
window.vpdLibraryPreview = function (container) {
  if (!container || container.dataset.vpdLibrary) return { dispose() { } };
  container.dataset.vpdLibrary = '1';
  const listeners = [];
  const on = (type, handler, options) => {
    container.addEventListener(type, handler, options);
    listeners.push([type, handler, options]);
  };
  const site = 'https://library.vcvrack.com/screenshots/';
  const missingKey = 'vpd-library-missing';
  const groupLimit = 12;
  const images = new Map();
  let missing;
  try { missing = new Set(JSON.parse(localStorage.getItem(missingKey) || '[]')); } catch { missing = new Set(); }
  const remember = () => { try { localStorage.setItem(missingKey, JSON.stringify([...missing])); } catch { } };
  const url = (key, size) => site + size + '/' + key.split('/').map(encodeURIComponent).join('/') + '.webp';
  const selector = '.lib[data-library], .lib[data-panels]';

  const card = document.createElement('div');
  card.className = 'vpd-preview';
  card.hidden = true;
  // On the body, out of the app's rendered markup; it takes the diagram's theme colors when shown.
  document.body.appendChild(card);
  let link = null, timer = 0;
  // How the last gesture started: 'mouse', 'touch' or 'pen'.
  let lastPointer = 'mouse';
  // The box's own tooltip, emptied while its icon shows a preview (the browser would draw it over the preview).
  let muted = null;

  function mute(target) {
    const title = target.closest('.vpd-node')?.querySelector(':scope > title');
    if (!title || title.textContent === '') return;
    muted = { title, text: title.textContent };
    title.textContent = '';
  }

  function unmute() {
    if (muted && muted.title.textContent === '') muted.title.textContent = muted.text;
    muted = null;
  }

  function hide() {
    clearTimeout(timer);
    unmute();
    link = null;
    card.hidden = true;
  }

  function place() {
    if (!link) return;
    // The app re-renders the diagram on a click (unfolding a group): the icon may be gone without a pointerout.
    if (!link.isConnected) { hide(); return; }
    const box = link.getBoundingClientRect();
    const width = card.offsetWidth, height = card.offsetHeight;
    // Beside the icon, on its right or else its left; on a phone, across the screen.
    const left = box.right + 8 + width <= window.innerWidth ? box.right + 8
      : box.left - 8 - width >= 4 ? box.left - 8 - width : Math.max(4, (window.innerWidth - width) / 2);
    card.style.left = left + 'px';
    card.style.top = Math.max(4, Math.min(window.innerHeight - height - 4, box.top + (box.height / 2) - (height / 2))) + 'px';
  }

  function markMissing(key) {
    container.querySelectorAll('.lib[data-library="' + CSS.escape(key) + '"]').forEach(l => l.classList.add('missing'));
  }

  // The image of one module, created on first use and shared by every preview showing it; null once known missing.
  function image(key) {
    if (missing.has(key)) return null;
    let img = images.get(key);
    if (!img) {
      img = new Image();
      img.alt = key;
      img.src = url(key, 200);
      img.srcset = url(key, 400) + ' 2x';
      img.addEventListener('load', place);
      img.addEventListener('error', () => {
        images.delete(key);
        missing.add(key);
        remember();
        markMissing(key);
        if (link) render(link);
      });
      images.set(key, img);
    }
    return img;
  }

  function note(text) {
    const div = document.createElement('div');
    div.className = 'note';
    div.textContent = text;
    return div;
  }

  function render(target) {
    const key = target.dataset.library;
    if (key) {
      const img = image(key);
      if (!img) markMissing(key);
      // With a finger, say how to open the page (the mouse has its pointer cursor for that).
      const hint = lastPointer === 'mouse' ? [] : [note('Tap again to open')];
      card.replaceChildren(...(img ? [img, ...hint] : ['Not in the VCV Library']));
    } else {
      // A group: each distinct module once, with a badge when there are several of it.
      const panels = target.dataset.panels.split(' ').filter(p => p).map(p => {
        const star = p.lastIndexOf('*');
        return { key: p.slice(0, star), count: +p.slice(star + 1) };
      });
      const shown = panels.slice(0, groupLimit);
      const others = panels.length - shown.length;
      let unlisted = +(target.dataset.unlisted || 0);
      const row = document.createElement('div');
      row.className = 'panels';
      shown.forEach(p => {
        const img = image(p.key);
        if (!img) { unlisted += p.count; return; }
        const figure = document.createElement('figure');
        figure.title = p.key;
        figure.appendChild(img);
        if (p.count > 1) {
          const badge = document.createElement('span');
          badge.className = 'count';
          badge.textContent = '×' + p.count;
          figure.appendChild(badge);
        }
        row.appendChild(figure);
      });
      const content = [row];
      if (others > 0) content.push(note('+ ' + others + ' other module' + (others > 1 ? 's' : '')));
      if (unlisted > 0) content.push(note('+ ' + unlisted + ' not in the VCV Library'));
      card.replaceChildren(...content);
    }
    place();
  }

  function show(target) {
    unmute();
    link = target;
    mute(target);
    const style = getComputedStyle(container);
    ['--vpd-panel', '--vpd-muted', '--vpd-ink'].forEach(v => card.style.setProperty(v, style.getPropertyValue(v)));
    card.hidden = false;
    render(target);
  }

  const iconOf = e => (e.target.closest && e.target.closest(selector)) || null;

  // Delegated, so it keeps working when the app re-renders the diagram. A short delay: sweeping the mouse across
  // the diagram doesn't fetch every panel it crosses.
  on('pointerover', e => {
    const target = iconOf(e);
    if (e.pointerType !== 'mouse' || !target || target === link) return;
    clearTimeout(timer);
    timer = setTimeout(() => { if (target.isConnected) show(target); }, 250);
  });
  on('pointerout', e => {
    const target = iconOf(e);
    // A finger "leaves" right after every tap: only the mouse hides the preview this way.
    if (e.pointerType !== 'mouse' || !target || (e.relatedTarget && target.contains(e.relatedTarget))) return;
    hide();
  });
  // A click (which may unfold or fold the box), a pan or a zoom: the preview no longer belongs where it is.
  // Except a second tap on the icon whose preview is shown: that one goes on (below).
  on('pointerdown', e => {
    lastPointer = e.pointerType;
    if (!(e.pointerType !== 'mouse' && link && iconOf(e) === link)) hide();
  }, { capture: true, passive: true });
  ['wheel', 'scroll'].forEach(type => on(type, hide, { capture: true, passive: true }));

  // No hover with a finger: the first tap on an icon shows its preview (and opens nothing), the second one opens the
  // Library page, or unfolds the group for a group's icon.
  on('click', e => {
    const target = iconOf(e);
    if (lastPointer === 'mouse' || !target) return;
    if (target === link && !card.hidden) {
      hide();
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    show(target);
  }, true);

  return {
    dispose() {
      hide();
      listeners.forEach(([type, handler, options]) => container.removeEventListener(type, handler, options));
      listeners.length = 0;
      card.remove();
      delete container.dataset.vpdLibrary;
    },
  };
};

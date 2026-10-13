// Interactivity for the static HTML export (a snapshot: folding/unfolding happens in the Blazor app, in C#).
(function () {
  const root = document.querySelector('.vpd-root');
  if (!root) return;
  const toggles = root.querySelectorAll('[data-layer-toggle]');
  const sync = () => toggles.forEach(t => root.classList.toggle('hide-' + t.dataset.layerToggle, !t.checked));
  toggles.forEach(t => t.addEventListener('change', sync));

  // Teaching path: 1 = audio only, 2 = + pitch, 3 = + modulation, 4 = + gate/trig/clock.
  root.querySelectorAll('[data-step]').forEach(b => b.addEventListener('click', () => {
    const step = +b.dataset.step;
    toggles.forEach((t, i) => { t.checked = i < step; });
    sync();
  }));

  const intents = root.querySelector('[data-intents-toggle]');
  if (intents) intents.addEventListener('change', () => root.classList.toggle('show-intents', intents.checked));

  // Zoom: real size by default, buttons and Ctrl+wheel, clamped to a readable range.
  const scroller = root.querySelector('.vpd-scroll');
  const svg = root.querySelector('.vpd-svg');
  const [, , width, height] = svg.getAttribute('viewBox').split(' ').map(Number);
  const label = root.querySelector('.vpd-zoom-label');
  // A phone: zoomed out further, and opened fitted to the width.
  const compact = window.vpdCompact();
  const minZoom = compact ? +root.dataset.minZoomCompact : +root.dataset.minZoom, maxZoom = +root.dataset.maxZoom;
  let zoom = 1;
  function setZoom(z) {
    zoom = Math.max(minZoom, Math.min(maxZoom, z));
    svg.style.width = (width * zoom) + 'px';
    svg.style.height = (height * zoom) + 'px';
    if (label) label.textContent = Math.round(zoom * 100) + '%';
  }
  root.querySelectorAll('[data-zoom]').forEach(b => b.addEventListener('click', () => {
    const z = b.dataset.zoom;
    setZoom(z === '+' ? zoom * 1.15 : z === '-' ? zoom / 1.15 : z === 'fit' ? window.vpdFitZoom(scroller, width, minZoom, maxZoom) : +z);
  }));
  window.vpdPanZoom(scroller, factor => setZoom(zoom * factor));
  setZoom(compact ? window.vpdFitZoom(scroller, width, minZoom, maxZoom) : 1);

  // Hovering a box follows its cables (shared with the app).
  window.vpdHoverHighlight(root);

  // Library links: the panel screenshot is fetched only when a link is hovered, then kept (and missing ones remembered).
  window.vpdLibraryPreview(root);

})();

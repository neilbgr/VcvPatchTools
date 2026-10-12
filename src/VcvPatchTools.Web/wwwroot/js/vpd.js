// Small browser helpers for the Blazor app: saving a generated file, and wiring the shared pan/zoom script.
window.vpdDownload = (fileName, mimeType, text) => window.vpdDownloadBlob(fileName, new Blob([text], { type: mimeType }));

// A converted patch: bytes from C# arrive as a Uint8Array.
window.vpdDownloadBytes = (fileName, mimeType, bytes) => window.vpdDownloadBlob(fileName, new Blob([bytes], { type: mimeType }));

window.vpdDownloadBlob = (fileName, blob) => {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
};

window.vpdScrollIntoView = element => element.scrollIntoView({ block: 'start' });

// Blazor side of panzoom.js: Ctrl+wheel zoom goes back to C# (PatchDiagramView.ZoomBy), which owns the zoom level.
window.vpdPanZoomDotNet = (scroller, view) =>
  window.vpdPanZoom(scroller, factor => view.invokeMethodAsync('ZoomBy', factor));

// A mouse or trackpad as the main pointer: a desktop, whose file dialog can filter on the .vcv extension.
window.vpdFinePointer = () => window.matchMedia('(pointer: fine)').matches;

// Scrolls the diagram vertically by dy pixels, and the page by whatever the diagram's own scroll box couldn't take
// (a diagram shorter than its box doesn't scroll).
window.vpdShiftScroll = (scroller, dy) => {
  const before = scroller.scrollTop;
  scroller.scrollTop = before + dy;
  const rest = dy - (scroller.scrollTop - before);
  if (Math.abs(rest) >= 1) window.scrollBy(0, rest);
};

// A box of the diagram (x, y, width, height in the scroll box's content) hidden by the details sheet at the bottom of the
// screen (see .vpd-panel in app.css) scrolls up above it.
window.vpdRevealAboveSheet = (scroller, x, y, width, height) => {
  const sheet = document.querySelector('.vpd-panel');
  if (!sheet || getComputedStyle(sheet).position !== 'fixed') return;
  // The room under the diagram first, or the scroll would stop short of it.
  window.vpdSheetRoom?.();
  const box = scroller.getBoundingClientRect();
  // The panel image has a fixed height: the sheet is already as tall as it will be.
  const visibleBottom = Math.min(box.bottom, sheet.getBoundingClientRect().top) - box.top;
  const top = y - scroller.scrollTop;
  const margin = 12;
  if (top + height + margin > visibleBottom) {
    scroller.scrollBy({ top: Math.min(top + height + margin - visibleBottom, top - margin), behavior: 'smooth' });
  }
  if (x - scroller.scrollLeft < 0 || x + width - scroller.scrollLeft > scroller.clientWidth) {
    scroller.scrollBy({ left: x - scroller.scrollLeft - margin, behavior: 'smooth' });
  }
};

// Drop-down menus are native <details>: a click outside one, or Escape, closes it.
document.addEventListener('click', e => {
  document.querySelectorAll('details.vpd-menu[open]').forEach(menu => { if (!menu.contains(e.target)) menu.open = false; });
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') document.querySelectorAll('details.vpd-menu[open]').forEach(menu => { menu.open = false; });
});

// Room under the diagram for the details sheet (see .vpd-scroll::after in app.css): none when the diagram fits above
// the sheet, otherwise the height the sheet covers. Kept up to date as the sheet opens, grows (its panel image loads),
// closes, and as the diagram is zoomed, folded or the window resized.
(() => {
  let frame = 0;
  const update = () => {
    frame = 0;
    const scroller = document.querySelector('.vpd-app .vpd-scroll');
    if (!scroller) return;
    const sheet = document.querySelector('.vpd-app .vpd-panel');
    const svg = scroller.querySelector('svg');
    let room = 0;
    if (sheet && svg) {
      const covered = Math.max(0, scroller.getBoundingClientRect().bottom - sheet.getBoundingClientRect().top);
      const visible = scroller.clientHeight - covered;
      if (svg.getBoundingClientRect().height > visible) room = covered;
    }
    const value = Math.round(room) + 'px';
    if (scroller.style.getPropertyValue('--sheet-room') !== value) scroller.style.setProperty('--sheet-room', value);
  };
  const schedule = () => { if (!frame) frame = requestAnimationFrame(update); };
  window.vpdSheetRoom = update;
  const resizes = new ResizeObserver(schedule);
  let watched = [];
  // Blazor adds and removes the sheet and the diagram: watch whichever are there now.
  new MutationObserver(() => {
    const now = [...document.querySelectorAll('.vpd-app .vpd-panel, .vpd-app .vpd-scroll, .vpd-app .vpd-scroll > svg')];
    if (now.length !== watched.length || now.some((e, i) => e !== watched[i])) {
      resizes.disconnect();
      now.forEach(e => resizes.observe(e));
      watched = now;
    }
    schedule();
  }).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('resize', schedule);
})();

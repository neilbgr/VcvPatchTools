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
  const box = scroller.getBoundingClientRect();
  // The sheet grows up to 45% of the screen (its panel image may still be loading): plan for its full height.
  const visibleBottom = Math.min(box.bottom, window.innerHeight * 0.55) - box.top;
  const top = y - scroller.scrollTop;
  const margin = 12;
  if (top + height + margin > visibleBottom) {
    scroller.scrollBy({ top: Math.min(top + height + margin - visibleBottom, top - margin), behavior: 'smooth' });
  }
  if (x - scroller.scrollLeft < 0 || x + width - scroller.scrollLeft > scroller.clientWidth) {
    scroller.scrollBy({ left: x - scroller.scrollLeft - margin, behavior: 'smooth' });
  }
};

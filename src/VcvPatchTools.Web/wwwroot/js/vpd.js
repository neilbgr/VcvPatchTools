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

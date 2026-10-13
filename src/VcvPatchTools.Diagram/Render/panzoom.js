// Pan (drag the background, or anywhere with a finger), Ctrl+wheel and two-finger pinch zoom for a diagram's scroll box.
// Shared: embedded in the static HTML export, and copied to the Blazor app's wwwroot/js at build time.
// Returns a handle whose dispose() removes it all (the app calls it when the diagram goes away).
window.vpdPanZoom = function (scroller, onZoom) {
  if (!scroller || scroller.dataset.vpdPan) return { dispose() { } };
  scroller.dataset.vpdPan = '1';
  const listeners = [];
  const on = (type, handler, options) => {
    scroller.addEventListener(type, handler, options);
    listeners.push([type, handler, options]);
  };
  let disposed = false;
  let drag = null;
  let suppressClick = false;
  // Fingers on the scroll box, for the pinch: pointerId → last position.
  const touches = new Map();
  let pinch = null;

  // Zooms by factor keeping the point (clientX, clientY) where it is on screen.
  function zoomAt(clientX, clientY, factor) {
    if (!onZoom || disposed) return;
    const rect = scroller.getBoundingClientRect();
    const px = clientX - rect.left + scroller.scrollLeft;
    const py = clientY - rect.top + scroller.scrollTop;
    const before = scroller.scrollWidth;
    return Promise.resolve(onZoom(factor)).then(() => requestAnimationFrame(() => {
      const ratio = scroller.scrollWidth / before;
      scroller.scrollLeft = px * ratio - (clientX - rect.left);
      scroller.scrollTop = py * ratio - (clientY - rect.top);
    }));
  }

  const spread = () => {
    const [a, b] = [...touches.values()];
    return { distance: Math.hypot(a.x - b.x, a.y - b.y), x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  };

  on('pointerdown', e => {
    // A new gesture: a click swallowed after the previous drag or pinch may never have come.
    if (touches.size === 0) suppressClick = false;
    if (e.pointerType === 'touch') {
      touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (touches.size === 2) {
        // A second finger: the pan becomes a pinch.
        drag = null;
        pinch = { distance: spread().distance, busy: false };
        suppressClick = true;
        return;
      }
    }
    // With a mouse, boxes and cables keep their own clicks (unfold, select) and only the background pans; a finger
    // pans from anywhere (a dense diagram has little background), a tap without moving still being a click.
    if (e.button !== 0 || (e.pointerType === 'mouse' && e.target.closest('.vpd-node, .vpd-edge'))) return;
    drag = { x: e.clientX, y: e.clientY, left: scroller.scrollLeft, top: scroller.scrollTop, moved: false, id: e.pointerId };
  });
  on('pointermove', e => {
    if (touches.has(e.pointerId)) touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pinch && touches.size === 2) {
      // One zoom step at a time: the app re-renders between steps.
      const now = spread();
      if (pinch.busy || now.distance <= 0 || pinch.distance <= 0) return;
      const factor = now.distance / pinch.distance;
      if (Math.abs(factor - 1) < 0.02) return;
      pinch.busy = true;
      pinch.distance = now.distance;
      Promise.resolve(zoomAt(now.x, now.y, factor)).finally(() => { if (pinch) pinch.busy = false; });
      return;
    }
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (!drag.moved && Math.abs(dx) + Math.abs(dy) > 4) {
      drag.moved = true;
      scroller.classList.add('panning');
      scroller.setPointerCapture(drag.id);
    }
    if (drag.moved) {
      scroller.scrollLeft = drag.left - dx;
      scroller.scrollTop = drag.top - dy;
    }
  });
  const end = e => {
    touches.delete(e.pointerId);
    if (touches.size < 2) pinch = null;
    if (drag && drag.moved) suppressClick = true;
    drag = null;
    scroller.classList.remove('panning');
  };
  on('pointerup', end);
  on('pointercancel', end);
  // A drag or a pinch must not also count as a click (which would fold the lane under the pointer).
  on('click', e => {
    if (suppressClick) { e.stopPropagation(); e.preventDefault(); suppressClick = false; }
  }, true);

  on('wheel', e => {
    if (!e.ctrlKey || !onZoom) return;
    e.preventDefault();
    zoomAt(e.clientX, e.clientY, e.deltaY < 0 ? 1.15 : 1 / 1.15);
  }, { passive: false });

  return {
    dispose() {
      disposed = true;
      listeners.forEach(([type, handler, options]) => scroller.removeEventListener(type, handler, options));
      listeners.length = 0;
      delete scroller.dataset.vpdPan;
      scroller.classList.remove('panning');
    },
  };
};

// Zoom that fits the diagram's width in the scroll box, never below the readable minimum.
window.vpdFitZoom = (scroller, width, minZoom, maxZoom) =>
  Math.max(minZoom, Math.min(maxZoom, (scroller.clientWidth - 2) / width));

// A phone or tablet, upright or on its side: narrow or short screen, or finger input. The diagram then fills the screen, opens fitted to its width and
// may be zoomed out further (to see it whole; pinch to read).
window.vpdCompact = () => window.matchMedia('(max-width: 700px), (max-height: 500px), (pointer: coarse)').matches;

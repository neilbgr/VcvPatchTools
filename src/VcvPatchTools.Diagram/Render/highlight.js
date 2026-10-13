// Hovering a box highlights it, its cables and the boxes at their other ends; everything else fades (see diagram.css).
// Shared: embedded in the static HTML export, and copied to the Blazor app's wwwroot/js at build time. Done here rather
// than in the app's C# so a hover doesn't re-render the whole diagram: only attributes the app doesn't render are set
// (data-hover on the .vpd-root, data-hv on the highlighted elements), so the app's own classes never fight with them.
// Returns a handle: refresh() after the diagram was redrawn, dispose() to remove it all.
window.vpdHoverHighlight = function (container) {
  if (!container) return { refresh() { }, dispose() { } };
  const root = container.closest('.vpd-root') || container;
  let current = null;

  function apply() {
    container.querySelectorAll('[data-hv]').forEach(e => e.removeAttribute('data-hv'));
    const node = current && [...container.querySelectorAll('.vpd-node[data-id]')].find(n => n.dataset.id === current);
    if (!node) {
      current = null;
      delete root.dataset.hover;
      return;
    }
    const linked = new Set([current]);
    container.querySelectorAll('.vpd-edge[data-from]').forEach(e => {
      if (e.dataset.from !== current && e.dataset.to !== current) return;
      e.dataset.hv = '';
      linked.add(e.dataset.from);
      linked.add(e.dataset.to);
    });
    container.querySelectorAll('.vpd-node[data-id]').forEach(n => { if (linked.has(n.dataset.id)) n.dataset.hv = ''; });
    root.dataset.hover = '';
  }

  const nodeOf = target => (target && target.closest && target.closest('.vpd-node[data-id]')) || null;
  // Delegated, so it keeps working when the app redraws the diagram.
  const over = e => {
    const node = nodeOf(e.target);
    if (!node || node.dataset.id === current) return;
    current = node.dataset.id;
    apply();
  };
  const out = e => {
    const node = nodeOf(e.target);
    if (!node || (e.relatedTarget && node.contains(e.relatedTarget))) return;
    current = null;
    apply();
  };
  container.addEventListener('mouseover', over);
  container.addEventListener('mouseout', out);

  return {
    refresh: apply,
    dispose() {
      container.removeEventListener('mouseover', over);
      container.removeEventListener('mouseout', out);
      current = null;
      apply();
    },
  };
};

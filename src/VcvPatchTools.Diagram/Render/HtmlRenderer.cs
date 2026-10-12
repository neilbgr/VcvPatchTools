using System.Net;
using System.Text;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Diagram.Render;

/// <summary>A single self-contained HTML file: the SVG, the stylesheet, a small script for layers and hover.</summary>
public static class HtmlRenderer
{
    public static string Render(DiagramLayout layout)
    {
        StringBuilder html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"color-scheme\" content=\"light dark\">");
        html.AppendLine($"<title>{WebUtility.HtmlEncode(layout.Title)}</title>");
        // "only light" keeps a phone's forced dark mode off our light theme (it would leave the SVG fills light).
        html.AppendLine($"<style>:root {{ color-scheme: only light; }} body {{ margin: 0; background: #eef0f2; }} @media (prefers-color-scheme: dark) {{ :root {{ color-scheme: dark; }} body {{ background: #16171a; }} }}\n{Resources.Css}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"<div class=\"vpd-root\" data-min-zoom=\"{Resources.MinZoom}\" data-max-zoom=\"{Resources.MaxZoom}\" data-min-zoom-compact=\"{Resources.MinZoomCompact}\"><div class=\"vpd-page\">"));
        html.AppendLine($"<h1>{WebUtility.HtmlEncode(layout.Title)}</h1>");
        html.AppendLine($"<p class=\"vpd-sub\">{layout.Groups.Sum(g => g.Members.Count)} modules in {layout.Groups.Count} groups <span class=\"vpd-long\">· drag the background to move around, hover a box to follow its cables. Stacked boxes are folded groups (open the patch in the app to unfold them).</span><span class=\"vpd-short\">· pinch to zoom, tap a box to follow its cables.</span></p>");
        html.AppendLine(Toolbar());
        html.AppendLine($"<div class=\"vpd-scroll\">{SvgRenderer.Render(layout, standalone: false)}</div>");
        if (layout.Diagnostics.Count > 0)
        {
            html.AppendLine("<ul class=\"vpd-diag\">");
            foreach (string diagnostic in layout.Diagnostics)
            {
                html.AppendLine($"<li>{WebUtility.HtmlEncode(diagnostic)}</li>");
            }
            html.AppendLine("</ul>");
        }
        html.AppendLine("</div></div>");
        html.AppendLine($"<script>{Resources.Script}</script>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static string Toolbar() =>
        """
        <div class="vpd-toolbar">
          <div class="group">Show:
            <button data-step="1">1 · audio</button><button data-step="2">2 · + pitch</button><button data-step="3">3 · + modulation</button><button data-step="4">4 · + gate/trig/clock</button>
          </div>
          <div class="group">
            <label><input type="checkbox" data-layer-toggle="audio" checked><span class="swatch" style="--c: var(--vpd-audio)"></span>audio</label>
            <label><input type="checkbox" data-layer-toggle="pitch" checked><span class="swatch" style="--c: var(--vpd-pitch)"></span>pitch</label>
            <label><input type="checkbox" data-layer-toggle="modulation" checked><span class="swatch" style="--c: var(--vpd-modulation)"></span>modulation</label>
            <label><input type="checkbox" data-layer-toggle="gate" checked><span class="swatch" style="--c: var(--vpd-gate)"></span>gate / trig / clock</label>
          </div>
          <div class="group"><label><input type="checkbox" data-intents-toggle> intents on every cable</label></div>
          <div class="group">Zoom:
            <button data-zoom="-" title="Zoom out (or Ctrl + wheel)">−</button><span class="vpd-zoom-label">100%</span><button data-zoom="+" title="Zoom in (or Ctrl + wheel)">+</button>
            <button data-zoom="1">100%</button><button data-zoom="fit" title="Fit the width, without going below a readable size">Fit</button>
          </div>
          <div class="group">Roles:
            <span class="role-time"><span class="swatch" style="--c: var(--role-color)"></span> time</span>
            <span class="role-pitch"><span class="swatch" style="--c: var(--role-color)"></span> pitch</span>
            <span class="role-controller"><span class="swatch" style="--c: var(--role-color)"></span> controller</span>
            <span class="role-source"><span class="swatch" style="--c: var(--role-color)"></span> source</span>
            <span class="role-modifier"><span class="swatch" style="--c: var(--role-color)"></span> modifier</span>
            <span class="role-effect"><span class="swatch" style="--c: var(--role-color)"></span> effect</span>
            <span class="role-mixer"><span class="swatch" style="--c: var(--role-color)"></span> mixer</span>
            <span class="role-performance"><span class="swatch" style="--c: var(--role-color)"></span> performance</span>
            <span class="role-io"><span class="swatch" style="--c: var(--role-color)"></span> I/O</span>
            <span class="role-monitor"><span class="swatch" style="--c: var(--role-color)"></span> monitor</span>
          </div>
        </div>
        """;
}
/* SPDX-License-Identifier: Apache-2.0 */
/* .ify baked report - capture + single-file assembly. Lives beside the live app but out
   of its way (the live page never carries the export code it rarely runs).

   bake(app, opts) assembles ONE self-contained HTML file from what the app already
   holds - the input values at capture, the watched outputs, and any element marked
   data-ify-bake (a viewport canvas becomes a JPEG, an SVG chart is inlined, a table is
   carried over) - then hands it to the browser as a download. The report is a frozen
   record: values render as text, never as controls, and the banner says so plainly.
   Opens anywhere with zero network requests: fonts are embedded base64, the capture
   theme's tokens are baked as literals.

   opts: root (default document) - where data-ify-bake elements are collected;
         notes (string) - an optional notes section;
         filename - override the default report-<doc>-<stamp>.html.
   Returns {size, warnings, status} - the caller surfaces a size warning past ~5 MB and
   shows status as its own line: the page can only know it GENERATED the file (the
   browser may still discard the download), and that sentence says so - a hand-written
   page drifted to "report saved" once because the copy was the page's to lose
   (round-7 finding 8). */

const TOKENS = [
  "--ify-bg", "--ify-surface", "--ify-surface-2", "--ify-ink", "--ify-ink-muted",
  "--ify-ink-subtle", "--ify-line", "--ify-line-strong", "--ify-accent",
  "--ify-accent-strong", "--ify-accent-muted", "--ify-warm", "--ify-warm-strong",
  "--ify-warm-surface", "--ify-on-warm", "--ify-font-display", "--ify-font-text",
  "--ify-font-mono", "--ify-text-xs", "--ify-text-sm", "--ify-text-base",
  "--ify-text-lg", "--ify-text-xl", "--ify-text-2xl", "--ify-tracking-tight",
  "--ify-space-1", "--ify-space-2", "--ify-space-3", "--ify-space-4", "--ify-space-5",
  "--ify-space-6", "--ify-space-7", "--ify-space-8",
  // The chart-text rule in ify-report.css resolves through this - without it a carried
  // chart's labels fall to the browser default face.
  "--ify-font-label",
];

const FONTS = [
  { family: "Jost Variable", file: "jost-latin-wght-normal.woff2", format: "woff2-variations", weight: "100 900" },
  { family: "Hanken Grotesk Variable", file: "hanken-grotesk-latin-wght-normal.woff2", format: "woff2-variations", weight: "100 900" },
  { family: "Fragment Mono", file: "fragment-mono-latin-400-normal.woff2", format: "woff2", weight: "400" },
];

const IMG_MAX_EDGE = 1600;
const IMG_QUALITY = 0.85;
const SIZE_WARN_BYTES = 5 * 1024 * 1024;

function esc(text) {
  return String(text ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

async function toBase64(url) {
  const resp = await fetch(url);
  if (!resp.ok) throw new Error(`fetch ${url}: ${resp.status}`);
  const buffer = await resp.arrayBuffer();
  let binary = "";
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

function captureCanvas(canvas) {
  const scale = Math.min(1, IMG_MAX_EDGE / Math.max(canvas.width, canvas.height, 1));
  if (scale >= 1) return canvas.toDataURL("image/jpeg", IMG_QUALITY);
  const off = document.createElement("canvas");
  off.width = Math.round(canvas.width * scale);
  off.height = Math.round(canvas.height * scale);
  off.getContext("2d").drawImage(canvas, 0, 0, off.width, off.height);
  return off.toDataURL("image/jpeg", IMG_QUALITY);
}

function controlValue(c) {
  switch (c.kind) {
    case "slider": case "knob":
      return c.evaluated == null ? String(c.value) : `${c.value} = ${c.evaluated}`;
    case "toggle": return c.bool ? "true" : "false";
    case "button": return c.bool ? "pressed" : "idle";
    case "valuelist": return c.text || (c.selected == null ? "" : `#${c.selected}`);
    case "panel": return c.text || "";
    case "mdslider": return (c.axes || []).map((a) => a.value).join(" , ");
    case "colour":
      return `<span class="ify-report__swatch" style="background:${esc((c.colour || "").slice(0, 7))}"></span>${esc(c.colour || "")}`;
    default: return esc(c.kind);
  }
}

export async function bake(app, opts = {}) {
  const root = opts.root || document;
  const warnings = [];

  // Fresh envelope: the values at capture plus the answering build's identity.
  const resp = await fetch(`/app/${app.homeId}/api/state`);
  if (!resp.ok) throw new Error(`state fetch failed (${resp.status}) - is Rhino open?`);
  const state = await resp.json();
  const doc = (state.docName || app.homeId.replace(/-[0-9a-f]{8,}$/i, "")).replace(/\.gh$/i, "");
  const now = new Date();
  const stamp = now.toISOString().slice(0, 16).replace("T", " ");

  // Theme is hard-baked: literal token values of the CURRENT resolved theme (an auto
  // report with light-ground captures looks broken on dark).
  const style = getComputedStyle(document.documentElement);
  const tokens = TOKENS
    .map((t) => ({ t, v: style.getPropertyValue(t).trim() }))
    .filter((x) => x.v)
    .map((x) => `  ${x.t}: ${x.v};`)
    .join("\n");

  const faces = [];
  for (const f of FONTS) {
    try {
      const b64 = await toBase64(`/app/${app.homeId}/kit/fonts/${f.file}`);
      faces.push(`@font-face { font-family: '${f.family}'; font-weight: ${f.weight}; font-display: swap;`
        + ` src: url(data:font/woff2;base64,${b64}) format('${f.format}'); }`);
    } catch {
      warnings.push(`font ${f.file} could not be embedded - the report falls back to system faces`);
    }
  }

  let reportCss = "";
  try {
    reportCss = await (await fetch(`/app/${app.homeId}/kit/report/ify-report.css`)).text();
  } catch {
    warnings.push("report stylesheet could not be read - the report is unstyled");
  }

  // Captures: anything the page marked data-ify-bake, in document order. A canvas
  // becomes pixels; an SVG (charts) inlines losslessly; tables carry over as markup.
  const figures = [];
  for (const el of root.querySelectorAll("[data-ify-bake]")) {
    const caption = el.getAttribute("data-ify-bake") || el.getAttribute("aria-label") || "";
    const canvas = el instanceof HTMLCanvasElement ? el : el.querySelector("canvas");
    if (canvas) {
      figures.push(`<figure><img src="${captureCanvas(canvas)}" alt="${esc(caption || "viewport capture")}">`
        + (caption ? `<figcaption>${esc(caption)}</figcaption>` : "") + "</figure>");
      continue;
    }
    const svg = el instanceof SVGElement ? el : el.querySelector("svg");
    const table = el.matches("table") ? el : el.querySelector("table");
    const body = svg ? svg.outerHTML : table ? table.outerHTML : null;
    if (body === null) {
      warnings.push("a data-ify-bake element had no canvas, svg, or table - skipped");
      continue;
    }
    figures.push(`<figure>${body}` + (caption ? `<figcaption>${esc(caption)}</figcaption>` : "") + "</figure>");
  }

  const inputRows = (state.controls || []).map((c) =>
    `<tr><td>${esc(c.nickName || c.id)}</td><td>${esc(c.kind)}</td><td class="num">${controlValue(c)}</td></tr>`).join("\n");

  const viewRows = (state.views || []).map((v) => {
    const samples = (v.samples || []).map((s) => esc(s.value)).join(" · ");
    const warn = (v.warnings || []).length
      ? `<div class="ify-report__warn">${esc(v.warnings.join("; "))}</div>` : "";
    return `<tr><td>${esc(v.param)}</td><td class="num">${v.tree ? `${v.tree.pathCount} / ${v.tree.dataCount}` : ""}</td>`
      + `<td>${samples}${warn}</td></tr>`;
  }).join("\n");

  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(doc)} — report</title>
<style>
:root {
${tokens}
}
${faces.join("\n")}
${reportCss}
</style>
</head>
<body class="ify-report">
<div class="ify-report__banner">static report · captured from ${esc(doc)}.gh on ${esc(stamp)} · not interactive</div>
<h1 class="ify-report__title">${esc(doc)}<span class="ext">.gh</span></h1>
<p class="ify-report__meta">captured via Wireify${state.wireify ? " " + esc(state.wireify) : ""}${state.docUnits ? " · document units " + esc(state.docUnits) : ""}${state.tolerance != null ? " · tolerance " + esc(state.tolerance) : ""}</p>
${figures.join("\n")}
${opts.notes ? `<div class="ify-report__section"><h2>notes</h2><p>${esc(opts.notes)}</p></div>` : ""}
<div class="ify-report__section">
<h2>inputs at capture</h2>
<table><thead><tr><th>input</th><th>kind</th><th>value</th></tr></thead>
<tbody>
${inputRows || "<tr><td colspan=\"3\">no controls declared</td></tr>"}
</tbody></table>
</div>
<div class="ify-report__section">
<h2>outputs at capture</h2>
<table><thead><tr><th>view</th><th>branches / items</th><th>samples</th></tr></thead>
<tbody>
${viewRows || "<tr><td colspan=\"3\">no views declared</td></tr>"}
</tbody></table>
</div>
<div class="ify-report__foot">
<span>static report — a frozen record of one state; the live companion app requires Wireify in Rhino</span>
<span>built with wireify · .ify</span>
</div>
</body>
</html>
`;

  const blob = new Blob([html], { type: "text/html" });
  if (blob.size > SIZE_WARN_BYTES)
    warnings.push(`report is ${(blob.size / 1048576).toFixed(1)} MB - fewer or smaller captures travel better`);

  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = opts.filename
    || `report-${doc}-${now.toISOString().slice(0, 16).replace(/[T:]/g, "-")}.html`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 10_000);

  return {
    size: blob.size,
    warnings,
    status: `report generated (${(blob.size / 1048576).toFixed(1)} MB) — check the browser's downloads`,
  };
}

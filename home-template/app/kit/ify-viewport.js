/* SPDX-License-Identifier: Apache-2.0 */
/* .ify app kit - the 3D viewport half. Renders the geometry payload the server serves at
   api/geometry (meshes as raw position/index buffers, curves as sampled polylines, points)
   inside an .ify-viewport frame, with orbit controls, named views, and fit-to-bounds.

   Interior styling is PROVISIONAL: a tokens-only neutral (surface ground, hairline grid,
   accent-tinted material, accent crease edges) until the canvas-seeded viewport language
   lands. The interior reads exactly four tokens - --ify-viewport-ground/-grid/-mesh/-edge
   in ify-tokens.css - so the seed re-skins it by editing values, never this file. An item
   may carry its own `color` (a CSS colour or a --token name) to be drawn apart from the
   rest - a member family, a flagged clash - and the tokens still paint everything else.
   The module boundary - create/update/fit/lookFrom and the payload shape - is the stable
   part.

   Pages that use it need an import map before any module script (bare "three" must
   resolve to the vendored build; OrbitControls imports it by that name):

     <script type="importmap">
       {"imports": {"three": "./kit/vendor/three.module.min.js"}}
     </script>
*/

import * as THREE from "three";
import { OrbitControls } from "./vendor/OrbitControls.js";

const FETCH_DEBOUNCE_MS = 150;
// Crease angle for the mesh edge pass: flat faces stay clean, every real corner outlines.
const EDGE_THRESHOLD_DEG = 25;
// The named views a page can switch to. Z is up, like the canvas; the top view needs its
// own up vector or the framing maths (right = forward x up) degenerates. "iso" is the
// default framing: Rhino's perspective quarter view.
const VIEWS = {
  iso: { dir: [1, -1, 0.8], up: [0, 0, 1], label: "3D" },
  top: { dir: [0, 0, 1], up: [0, 1, 0], label: "top" },
  front: { dir: [0, -1, 0], up: [0, 0, 1], label: "front" },
  side: { dir: [1, 0, 0], up: [0, 0, 1], label: "side" },
};
const Z_UP = new THREE.Vector3(0, 0, 1);

/* Resolve a CSS colour to an sRGB number for three.js - via a 2D canvas so oklch()
   tokens (which getComputedStyle hands back unresolved) still work. A value the canvas
   cannot parse yields null, so a typo can fall back instead of painting black. */
let probeCtx = null;
function parseColor(raw) {
  probeCtx = probeCtx || document.createElement("canvas").getContext("2d", { willReadFrequently: true });
  probeCtx.fillStyle = "#010203"; // a sentinel no real colour hits; an invalid assignment leaves it
  probeCtx.fillStyle = String(raw || "").trim();
  if (probeCtx.fillStyle === "#010203") return null;
  probeCtx.clearRect(0, 0, 1, 1);
  probeCtx.fillRect(0, 0, 1, 1);
  const d = probeCtx.getImageData(0, 0, 1, 1).data;
  return (d[0] << 16) | (d[1] << 8) | d[2];
}
function tokenColor(name) {
  return parseColor(getComputedStyle(document.documentElement).getPropertyValue(name));
}
function cssColor(name, fallback) {
  const parsed = tokenColor(name);
  return parsed === null ? (parseColor(fallback) ?? 0) : parsed;
}
/* An item's own colour: a --token name resolves through the theme (and re-resolves on a
   theme flip), anything else parses as CSS; unparsable keeps the palette. */
function itemColor(spec, fallback) {
  if (!spec) return fallback;
  const s = String(spec).trim();
  const parsed = s.startsWith("--") ? tokenColor(s) : parseColor(s);
  return parsed === null ? fallback : parsed;
}
function darken(rgb, k) {
  const r = Math.round(((rgb >> 16) & 255) * k);
  const g = Math.round(((rgb >> 8) & 255) * k);
  const b = Math.round((rgb & 255) * k);
  return (r << 16) | (g << 8) | b;
}

function palette() {
  return {
    ground: cssColor("--ify-viewport-ground", "#f5f7f7"),
    grid: cssColor("--ify-viewport-grid", "#e2e6e6"),
    mesh: cssColor("--ify-viewport-mesh", "#dbeaea"),
    edge: cssColor("--ify-viewport-edge", "#3f6f6f"),
  };
}

/* Create a viewport inside el (an .ify-viewport__canvas or any block element). Options:
   viewsEl - an element the module fills with the named-view buttons (3D, top, front,
   side) and fit; hint - false to skip the interaction overlay (a translucent card with
   the orbit glyph, shown on the first geometry for two seconds or until the first grab);
   intro - false to skip the settle-in motion (the first fit arrives from a few degrees
   off, so a still frame reads as a model that can be turned; skipped under
   prefers-reduced-motion); holding - a function the viewport asks before re-framing,
   true while the user holds a control (bind wires it to the app's editingAny).
   Returns {update(geometry), fit(), autoFit(), lookFrom(name), fly(legs), bounds(),
   view(), dispose(), canvas}. */
export function create(el, opts = {}) {
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 10000);
  camera.up.set(0, 0, 1); // Rhino is Z-up; the viewport must agree with the canvas
  camera.position.set(15, -15, 12);

  // preserveDrawingBuffer so the report bake can capture the canvas as pixels.
  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
  el.replaceChildren(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  // Arm on actual camera MOVEMENT inside a gesture, not bare pointer-down: OrbitControls'
  // "start" fires on any click, and a stray click must not disarm auto-refit for the
  // life of the page (round-6 S6.7c). Damping's inertia fires "change" after "end" with
  // interacting already false, so it never arms either.
  let interacting = false;
  let userMoved = false;
  let fitted = false;
  let currentView = "iso";
  // A change that arrived under a held control owes a settle once the hold ends.
  let pendingSettle = false;
  let flight = null; // a page-driven camera path; a grab cancels it
  let dolly = null; // the eased camera move a settle runs, cancelled by a grab
  const SETTLE_CLIP = 1.0;  // a point beyond this is outside the frame
  // The model's projected span, where the whole frame is 2 and a fit lands near 1.8.
  // Below this the frame has gone slack and the camera owes the model a re-frame.
  const SETTLE_SPAN = 1.2;
  const DOLLY_MS = 420;
  controls.addEventListener("start", () => {
    interacting = true;
    hideHint();
    intro = null; // the user has the camera now
    dolly = null;
    cancelFlight();
    // Orbit always spins about Z: a grab from the top view drops back to Z-up first.
    if (!camera.up.equals(Z_UP)) camera.up.copy(Z_UP);
  });
  controls.addEventListener("end", () => { interacting = false; });
  controls.addEventListener("change", () => {
    if (!interacting) return;
    userMoved = true;
    markView();
  });

  // The interaction overlay: a translucent card over the canvas with the orbit glyph and
  // the gestures, shown when the first geometry lands and gone two seconds later or at
  // the first grab, whichever comes first. Touch gets its own wording (two fingers pan
  // and zoom under OrbitControls).
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
  let hint = null;
  let hintTimer = 0;
  if (opts.hint !== false) {
    hint = document.createElement("div");
    hint.className = "ify-viewport__hint";
    hint.hidden = true;
    const card = document.createElement("div");
    card.className = "ify-viewport__hint-card";
    card.innerHTML = '<svg viewBox="0 0 48 48" width="40" height="40" aria-hidden="true">'
      + '<ellipse cx="24" cy="24" rx="19" ry="8" fill="none" stroke="currentColor" stroke-width="2" stroke-dasharray="4 5"/>'
      + '<circle cx="24" cy="24" r="8" fill="none" stroke="currentColor" stroke-width="2"/>'
      + '<path d="M40 19 L43 24 L37 24 Z" fill="currentColor"/>'
      + '</svg>';
    const text = document.createElement("span");
    text.textContent = matchMedia("(pointer: coarse)").matches
      ? "drag to orbit · two fingers to pan and zoom"
      : "drag to orbit · right-drag to pan · scroll to zoom";
    card.append(text);
    hint.append(card);
    el.append(hint);
  }
  function showHint() {
    if (!hint || !hint.hidden) return;
    hint.hidden = false;
    hintTimer = setTimeout(hideHint, 2400);
  }
  function hideHint() {
    if (!hint) return;
    clearTimeout(hintTimer);
    const h = hint;
    hint = null;
    if (h.hidden) { h.remove(); return; }
    h.setAttribute("data-gone", "");
    setTimeout(() => h.remove(), 400);
  }

  // The settle-in motion: the first framing starts a few degrees around the model's
  // axis and eases into place, so a page that has just loaded shows a model that turns.
  const INTRO_MS = 1400;
  const INTRO_YAW = (-22 * Math.PI) / 180;
  let intro = null; // {t0, target, offset}
  function startIntro() {
    if (opts.intro === false || reduceMotion) return;
    intro = { t0: performance.now(), target: controls.target.clone(), offset: camera.position.clone().sub(controls.target) };
  }
  function stepIntro(now) {
    if (!intro) return;
    const k = Math.min(1, (now - intro.t0) / INTRO_MS);
    const ease = 1 - Math.pow(1 - k, 3);
    const pos = intro.offset.clone().applyAxisAngle(Z_UP, INTRO_YAW * (1 - ease)).add(intro.target);
    camera.position.copy(pos);
    if (k >= 1) intro = null;
  }

  // Named views + fit, rendered where the page points (a .ify-viewport__views span).
  const viewButtons = new Map();
  if (opts.viewsEl) {
    const frag = document.createDocumentFragment();
    for (const name of Object.keys(VIEWS)) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "ify-viewport__action";
      b.dataset.view = name;
      b.textContent = VIEWS[name].label;
      b.addEventListener("click", () => lookFrom(name));
      viewButtons.set(name, b);
      frag.append(b);
    }
    const fitBtn = document.createElement("button");
    fitBtn.type = "button";
    fitBtn.className = "ify-viewport__action";
    fitBtn.textContent = "fit";
    fitBtn.addEventListener("click", () => { cancelFlight("fit"); fit(); });
    frag.append(fitBtn);
    opts.viewsEl.replaceChildren(frag);
  }
  function markView() {
    for (const [name, b] of viewButtons)
      b.setAttribute("aria-pressed", name === currentView && !userMoved ? "true" : "false");
  }
  markView();

  let colors = palette();
  scene.background = new THREE.Color(colors.ground);

  // Grid sized and seated from the model itself once bounds exist: a fixed square at the
  // world origin reads as a stray rectangle beside any model that lives elsewhere — or
  // sits entirely off-screen (round-6 S6.7c).
  let grid = null;
  function updateGrid() {
    if (grid) {
      scene.remove(grid);
      grid.geometry.dispose();
      grid.material.dispose();
    }
    let size = 50, cx = 0, cy = 0, cz = 0;
    if (bounds) {
      size = Math.max(bounds[3] - bounds[0], bounds[4] - bounds[1], 1) * 1.5;
      cx = (bounds[0] + bounds[3]) / 2;
      cy = (bounds[1] + bounds[4]) / 2;
      cz = bounds[2]; // seat it at the model's underside
    }
    grid = new THREE.GridHelper(size, 20, colors.grid, colors.grid);
    grid.rotation.x = Math.PI / 2; // GridHelper is XZ; rotate onto Rhino's XY ground
    grid.position.set(cx, cy, cz);
    scene.add(grid);
  }
  scene.add(new THREE.AmbientLight(0xffffff, 0.75));
  const sun = new THREE.DirectionalLight(0xffffff, 1.4);
  sun.position.set(1, -0.7, 1.4);
  scene.add(sun);

  const content = new THREE.Group();
  scene.add(content);
  let bounds = null;
  let framePoints = []; // the payload's own coordinates, for framing the silhouette
  // Auto-fit stays live until the user grabs the camera: the first fit races page layout
  // on content-heavy pages (fonts, charts, tables still settling), so a fit computed
  // against a stale canvas size framed the model off-screen while every status line said
  // success (round-5 S5.7a). Every resize re-fits until the first orbit; after that the
  // camera's ANGLE is the user's - geometry changes still re-frame at that angle.
  updateGrid(); // the origin-anchored default until the first geometry arrives

  function meshMaterial(color) {
    return new THREE.MeshStandardMaterial({
      color, metalness: 0, roughness: 0.85,
      side: THREE.DoubleSide, flatShading: true,
    });
  }

  function clearContent() {
    for (const child of [...content.children]) {
      content.remove(child);
      if (child.geometry) child.geometry.dispose();
      if (child.material) child.material.dispose();
    }
  }

  function update(geo) {
    clearContent();
    bounds = geo && geo.bounds && geo.bounds.length === 6 ? geo.bounds : null;
    framePoints = [];
    updateGrid();
    if (!geo) return;
    for (const m of geo.meshes || []) {
      const g = new THREE.BufferGeometry();
      g.setAttribute("position", new THREE.Float32BufferAttribute(m.positions, 3));
      g.setIndex(m.indices);
      g.computeVertexNormals();
      const fill = itemColor(m.color, colors.mesh);
      const mesh = new THREE.Mesh(g, meshMaterial(fill));
      mesh.userData.colorSpec = m.color || null;
      content.add(mesh);
      // Crease edges in the one colour with real contrast. Meshes carried no edge pass, so
      // a mesh-only view (a solids hero - the common case) drew the accent NOWHERE while
      // fill and grid sat within 0.02 lightness of each other in both themes (round-7
      // finding 2). The seed decides the fill and grid values; this pass is the floor. An
      // item with its own colour outlines in a darker shade of it.
      const edges = new THREE.LineSegments(
        new THREE.EdgesGeometry(g, EDGE_THRESHOLD_DEG),
        new THREE.LineBasicMaterial({ color: m.color ? darken(fill, 0.55) : colors.edge }));
      edges.userData.colorSpec = m.color || null;
      edges.userData.isEdge = true;
      content.add(edges);
      framePoints.push(m.positions);
    }
    for (const c of geo.curves || []) {
      if (c.radius > 0) {
        // A curve with a radius reads as a thin tube: WebGL draws lines one pixel wide
        // whatever the zoom, so a cable next to a strut needs a body of its own.
        const fill = itemColor(c.color, colors.edge);
        for (let i = 0; i + 5 < c.points.length; i += 3) {
          const a = new THREE.Vector3(c.points[i], c.points[i + 1], c.points[i + 2]);
          const b = new THREE.Vector3(c.points[i + 3], c.points[i + 4], c.points[i + 5]);
          const len = a.distanceTo(b);
          if (len <= 0) continue;
          const g = new THREE.CylinderGeometry(c.radius, c.radius, len, 10, 1, true);
          const seg = new THREE.Mesh(g, meshMaterial(fill));
          seg.position.copy(a).lerp(b, 0.5);
          seg.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), b.clone().sub(a).normalize());
          seg.userData.colorSpec = c.color || null;
          seg.userData.isCurve = true;
          content.add(seg);
        }
      } else {
        const g = new THREE.BufferGeometry();
        g.setAttribute("position", new THREE.Float32BufferAttribute(c.points, 3));
        const line = new THREE.Line(g, new THREE.LineBasicMaterial({ color: itemColor(c.color, colors.edge) }));
        line.userData.colorSpec = c.color || null;
        content.add(line);
      }
      framePoints.push(c.points);
    }
    // Markers: spheres a page places on the model (a clash point, a support, a joint to
    // call out). They ride outside the item counts and never drive the framing.
    for (const mk of geo.markers || []) {
      if (!mk || !mk.at || !(mk.radius > 0)) continue;
      const g = new THREE.SphereGeometry(mk.radius, 14, 10);
      const ball = new THREE.Mesh(g, meshMaterial(itemColor(mk.color, colors.edge)));
      ball.position.set(mk.at[0], mk.at[1], mk.at[2]);
      ball.userData.colorSpec = mk.color || null;
      ball.userData.isMarker = true;
      content.add(ball);
    }
    if ((geo.points || []).length) {
      const g = new THREE.BufferGeometry();
      g.setAttribute("position", new THREE.Float32BufferAttribute(geo.points, 3));
      content.add(new THREE.Points(g, new THREE.PointsMaterial({ color: colors.edge, size: 4, sizeAttenuation: false })));
      framePoints.push(geo.points);
    }
    // The camera holds still while the user holds a control: a camera that pulls back as
    // the model grows reads as the model's own parts getting thinner, so a ring radius
    // looked like it was driving the strut radius (HALO, 2026-09-15). What moves under a
    // drag is the model. The framing settles when the hand comes off - and only then if
    // the model has left the frame or shrunk deep inside it. A camera mid-orbit is left
    // alone either way.
    if (!fitted || interacting || flight) return;
    if (opts.holding && opts.holding()) { pendingSettle = true; return; }
    pendingSettle = false;
    settle();
  }

  /* Where the model sits in the frame: `edge` is the furthest normalized coordinate any
     of its points reaches, so above 1 it is clipped, and `span` is how much of the frame
     its projection actually covers, where the whole frame is 2. Measuring only the edge
     confuses a small model parked off to one side with one that fills the view: after a
     long rope became a short one the camera kept the old target, and a model covering a
     sixth of the frame still scored 0.76 (HALO, 2026-09-16). */
  function framing() {
    if (!bounds) return { edge: 1, span: 2 };
    const sets = framePoints.length ? framePoints : [cornerPoints(bounds)];
    const away = camera.position.clone().sub(controls.target);
    const dist = away.length();
    if (!(dist > 0)) return { edge: 1, span: 2 };
    const forward = away.clone().negate().normalize();
    let right = new THREE.Vector3().crossVectors(forward, camera.up);
    if (right.lengthSq() < 1e-9) right = new THREE.Vector3().crossVectors(forward, new THREE.Vector3(0, 1, 0));
    right.normalize();
    const up = new THREE.Vector3().crossVectors(right, forward);
    const vFov = (camera.fov * Math.PI) / 180;
    const hFov = 2 * Math.atan(Math.tan(vFov / 2) * Math.max(camera.aspect, 0.01));
    const th = Math.tan(hFov / 2);
    const tv = Math.tan(vFov / 2);
    const p = new THREE.Vector3();
    let edge = 0;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const pts of sets)
      for (let i = 0; i + 2 < pts.length; i += 3) {
        p.set(pts[i], pts[i + 1], pts[i + 2]).sub(controls.target);
        const d = Math.max(dist + p.dot(forward), 1e-6);
        const sx = p.dot(right) / (d * th);
        const sy = p.dot(up) / (d * tv);
        edge = Math.max(edge, Math.abs(sx), Math.abs(sy));
        if (sx < minX) minX = sx;
        if (sx > maxX) maxX = sx;
        if (sy < minY) minY = sy;
        if (sy > maxY) maxY = sy;
      }
    return { edge, span: Math.max(maxX - minX, maxY - minY) };
  }

  /* Out of frame, or so small the view has gone slack: the two cases worth taking the
     camera for. In between, a model that reads bigger IS bigger - the honest reading. */
  function settle() {
    if (flight) return;
    const view = framing();
    if (view.edge <= SETTLE_CLIP && view.span >= SETTLE_SPAN) return;
    if (reduceMotion) { fit(); return; }
    const fromPos = camera.position.clone();
    const fromTarget = controls.target.clone();
    fit();
    dolly = {
      t0: performance.now(),
      fromPos, fromTarget,
      toPos: camera.position.clone(),
      toTarget: controls.target.clone(),
    };
    camera.position.copy(fromPos);
    controls.target.copy(fromTarget);
  }

  /* Fly the camera along a path the page composes: legs of {position, target, ms, ease},
     one after another. A leg eases in and out by default; `ease: "out"` starts at speed and
     settles, which is what a leg wants when the one before it left the camera in place -
     a slow start there reads as the button doing nothing. The kit carries the camera and knows nothing about
     what the model means; a page that knows its own geometry (a rope that runs along x,
     a corridor, a truss) builds the path. A grab cancels, reduced motion lands on the
     last leg at once, and the returned promise settles either way. */
  function cancelFlight(reason) {
    if (!flight) return;
    const done = flight.resolve;
    flight = null;
    done(reason || "cancelled");
  }

  function fly(legs) {
    return new Promise((resolve) => {
      const path = (legs || []).filter((leg) => leg && leg.position && leg.target);
      if (!path.length) { resolve("empty"); return; }
      const last = path[path.length - 1];
      if (reduceMotion) {
        camera.position.fromArray(last.position);
        controls.target.fromArray(last.target);
        resolve("reduced");
        return;
      }
      // Inside a model the near plane of a fit-sized camera would clip everything away.
      camera.near = 0.02;
      camera.far = Math.max(camera.far, 10000);
      camera.updateProjectionMatrix();
      flight = {
        legs: path, i: 0, t0: performance.now(), resolve,
        fromPos: camera.position.clone(), fromTarget: controls.target.clone(),
      };
    });
  }

  const legPos = new THREE.Vector3();
  const legTarget = new THREE.Vector3();
  function stepFlight(now) {
    if (!flight) return;
    const leg = flight.legs[flight.i];
    const k = Math.min(1, (now - flight.t0) / Math.max(1, leg.ms || 1200));
    const ease = leg.ease === "out"
      ? 1 - Math.pow(1 - k, 3)
      : leg.ease === "in"
        ? k * k * k
        : k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
    camera.position.lerpVectors(flight.fromPos, legPos.fromArray(leg.position), ease);
    controls.target.lerpVectors(flight.fromTarget, legTarget.fromArray(leg.target), ease);
    if (k < 1) return;
    flight.i += 1;
    if (flight.i >= flight.legs.length) {
      const done = flight.resolve;
      flight = null;
      done("done");
      return;
    }
    flight.t0 = now;
    flight.fromPos.copy(camera.position);
    flight.fromTarget.copy(controls.target);
  }

  function stepDolly(now) {
    if (!dolly) return;
    const k = Math.min(1, (now - dolly.t0) / DOLLY_MS);
    const ease = 1 - Math.pow(1 - k, 3);
    camera.position.lerpVectors(dolly.fromPos, dolly.toPos, ease);
    controls.target.lerpVectors(dolly.fromTarget, dolly.toTarget, ease);
    if (k >= 1) dolly = null;
  }

  function cornerPoints(b) {
    const out = [];
    for (const x of [b[0], b[3]])
      for (const y of [b[1], b[4]])
        for (const z of [b[2], b[5]]) out.push(x, y, z);
    return out;
  }

  /* Solve a framing without touching the camera: frame the model's OWN points (mesh
     vertices, curve samples, points) and fall back to the 8 bbox corners only when the
     payload carries none - projecting the corners is exact for the BOX, but a sparse
     model's silhouette sits well inside it (a 1.6 x 200 truss went from ~31% of the
     canvas on the bounding sphere to ~51% on the corners - round-6 S6.7a - and to more
     here). And the bbox centre is not the silhouette's centre under perspective: one edge
     bound at the padding while the opposite kept slack (8% left, 9.5% low - round-7
     finding 12). So: pass 1 solves the distance and re-centres the target on the projected
     extents' midpoint; pass 2 solves the distance again. Deterministic - the same payload
     and canvas frame identically on every load. */
  function solveFraming(dirArray, upVector) {
    if (!bounds) return null;
    const sets = framePoints.length ? framePoints : [cornerPoints(bounds)];
    const target = new THREE.Vector3(
      (bounds[0] + bounds[3]) / 2, (bounds[1] + bounds[4]) / 2, (bounds[2] + bounds[5]) / 2);
    const dir = new THREE.Vector3().fromArray(dirArray).normalize();
    const forward = dir.clone().negate();
    let right = new THREE.Vector3().crossVectors(forward, upVector);
    if (right.lengthSq() < 1e-9) right = new THREE.Vector3().crossVectors(forward, new THREE.Vector3(0, 1, 0));
    right.normalize();
    const up = new THREE.Vector3().crossVectors(right, forward);
    const vFov = (camera.fov * Math.PI) / 180;
    const hFov = 2 * Math.atan(Math.tan(vFov / 2) * Math.max(camera.aspect, 0.01));
    const th = Math.tan(hFov / 2);
    const tv = Math.tan(vFov / 2);
    const p = new THREE.Vector3();
    let dist = 0.5;
    for (let pass = 0; pass < 2; pass++) {
      dist = 0.5;
      for (const pts of sets)
        for (let i = 0; i + 2 < pts.length; i += 3) {
          p.set(pts[i], pts[i + 1], pts[i + 2]).sub(target);
          const depth = p.dot(forward); // negative = nearer the camera than the target
          dist = Math.max(dist, Math.abs(p.dot(right)) / th - depth, Math.abs(p.dot(up)) / tv - depth);
        }
      dist *= 1.1; // honest padding, not slack on top of slack
      if (pass === 1) break;
      let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
      for (const pts of sets)
        for (let i = 0; i + 2 < pts.length; i += 3) {
          p.set(pts[i], pts[i + 1], pts[i + 2]).sub(target);
          const d = Math.max(dist + p.dot(forward), 1e-6); // distance from the camera along the view
          const sx = p.dot(right) / (d * th);
          const sy = p.dot(up) / (d * tv);
          if (sx < minX) minX = sx;
          if (sx > maxX) maxX = sx;
          if (sy < minY) minY = sy;
          if (sy > maxY) maxY = sy;
        }
      target.addScaledVector(right, ((minX + maxX) / 2) * dist * th)
        .addScaledVector(up, ((minY + maxY) / 2) * dist * tv);
    }
    return { position: target.clone().addScaledVector(dir, dist), target, dist };
  }

  function applyFraming(framing) {
    if (!framing) return;
    controls.target.copy(framing.target);
    camera.position.copy(framing.position);
    camera.near = Math.max(framing.dist / 1000, 0.001);
    camera.far = framing.dist * 100;
    camera.updateProjectionMatrix();
  }

  function fit(direction) {
    // The framing direction: a named view's, the user's own once they have orbited, the
    // default quarter view otherwise. Fit never steals the angle, only the distance.
    const dirArray = direction
      ? direction
      : userMoved
        ? camera.position.clone().sub(controls.target).normalize().toArray()
        : VIEWS[currentView].dir;
    applyFraming(solveFraming(dirArray, camera.up));
  }

  /* Where a named view would land, without going there: a page flying its own path ends
     ON the view instead of near it, so nothing jumps when the path finishes. */
  function poseFor(name) {
    const v = VIEWS[name];
    if (!v) return null;
    const framing = solveFraming(v.dir, new THREE.Vector3().fromArray(v.up));
    return framing ? { position: framing.position.toArray(), target: framing.target.toArray() } : null;
  }

  function fitAuto() {
    fit();
    const first = !fitted;
    fitted = true;
    if (first) {
      showHint();
      startIntro();
    }
  }

  /* Frame the model from a named view. The camera is the user's again only when they
     next orbit; until then geometry changes keep re-framing this view. */
  function lookFrom(name) {
    const v = VIEWS[name];
    if (!v) return;
    // A named view during a flight is the user changing their mind: the path stops here.
    cancelFlight("view");
    currentView = name;
    userMoved = false;
    camera.up.set(v.up[0], v.up[1], v.up[2]);
    fit(v.dir);
    fitted = true;
    markView();
  }

  function resize() {
    const w = el.clientWidth || 1;
    const h = el.clientHeight || 1;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    // Layout settling re-frames for free: the one fit computed against a half-laid-out
    // canvas is corrected by the next real size, until the user takes the camera.
    if (fitted && !userMoved && bounds && !flight) fit();
  }
  const ro = new ResizeObserver(resize);
  ro.observe(el);
  resize();

  // Theme flips re-resolve the tokens: background, grid, and materials follow the page -
  // an item's own --token colour re-resolves with them.
  const retheme = () => {
    colors = palette();
    scene.background = new THREE.Color(colors.ground);
    updateGrid(); // rebuilt in place - same bounds, fresh grid color
    for (const child of content.children) {
      const spec = child.userData.colorSpec;
      if (child.isMesh) child.material.color.set(itemColor(spec, child.userData.isCurve || child.userData.isMarker ? colors.edge : colors.mesh));
      else if (child.userData.isEdge) child.material.color.set(spec ? darken(itemColor(spec, colors.mesh), 0.55) : colors.edge);
      else if (child.material) child.material.color.set(itemColor(spec, colors.edge));
    }
  };
  const themeObserver = new MutationObserver(retheme);
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  const media = matchMedia("(prefers-color-scheme: dark)");
  media.addEventListener("change", retheme);

  let disposed = false;
  (function loop(now) {
    if (disposed) return;
    requestAnimationFrame(loop);
    // A hold can end without another payload - a value dropped back where it started
    // never re-solves - so the settle is driven from here, not from update() alone.
    if (pendingSettle && !(opts.holding && opts.holding())) {
      pendingSettle = false;
      if (!interacting) settle();
    }
    stepIntro(now || performance.now());
    stepDolly(now || performance.now());
    stepFlight(now || performance.now());
    controls.update();
    renderer.render(scene, camera);
  })(performance.now());

  return {
    update,
    fit,
    // bind's first-geometry fit: arms the settle-refit and the re-frame on change, where
    // a bare fit() (the user's own button) only re-frames once.
    autoFit: fitAuto,
    lookFrom,
    fly,
    /* Where a named view would land, and where the camera is now: a page composing a
       path needs both, to end exactly on a view and to start from the nearer end. */
    poseFor,
    pose: () => ({ position: camera.position.toArray(), target: controls.target.toArray() }),
    /* The payload's own extents, for a page composing a camera path. */
    bounds: () => (bounds ? bounds.slice() : null),
    view: () => (userMoved ? null : currentView),
    canvas: renderer.domElement,
    dispose() {
      disposed = true;
      ro.disconnect();
      themeObserver.disconnect();
      media.removeEventListener("change", retheme);
      clearContent();
      controls.dispose();
      renderer.dispose();
    },
  };
}

/* Bind a viewport to a geometry-marked view: fetch api/geometry now and again after every
   state frame (debounced - a drag's frames collapse to one trailing fetch), render honest
   status into statusEl (item counts, budget warnings, failures), fit the camera on the
   first geometry to arrive. viewsEl / hint pass through to create(). */
export function bind(app, viewId, el, opts = {}) {
  const vp = create(el, Object.assign(
    { holding: () => !!(app.editingAny && app.editingAny()) }, opts));
  const statusEl = opts.statusEl || null;
  const url = `/app/${app.homeId}/api/geometry?id=${encodeURIComponent(viewId)}`
    + (opts.param ? `&param=${encodeURIComponent(opts.param)}` : "");

  let timer = 0;
  let fitted = false;
  let inflight = false;
  let again = false;

  async function fetchNow() {
    if (inflight) { again = true; return; }
    inflight = true;
    try {
      const resp = await fetch(url);
      const body = await resp.json().catch(() => ({}));
      if (!resp.ok) {
        if (statusEl) statusEl.textContent = body.error || `geometry unavailable (${resp.status})`;
        return;
      }
      vp.update(body);
      if (!fitted && body.bounds) { vp.autoFit(); fitted = true; }
      if (statusEl) {
        const parts = [`${body.renderedCount} of ${body.itemCount} item(s)`];
        for (const w of body.warnings || []) parts.push(w);
        statusEl.textContent = parts.join(" · ");
      }
    } catch {
      if (statusEl) statusEl.textContent = "geometry fetch failed - is Rhino still open?";
    } finally {
      inflight = false;
      if (again) { again = false; fetchNow(); }
    }
  }

  const unsub = app.onState(() => {
    clearTimeout(timer);
    timer = setTimeout(fetchNow, FETCH_DEBOUNCE_MS);
  });
  fetchNow();

  return {
    viewport: vp,
    refresh: fetchNow,
    dispose() { clearTimeout(timer); unsub(); vp.dispose(); },
  };
}

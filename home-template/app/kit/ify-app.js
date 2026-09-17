/* SPDX-License-Identifier: Apache-2.0 */
/* .ify app kit - behaviors. Two layers, deliberately separate:

   1. The headless binding core (connect): the API client, value state, SSE stream with
      reconnect and multi-tab sharing, push debounce/echo handling, and honest status
      mapping. It builds NO DOM and assumes nothing about presentation - a fully custom
      frontend uses the core directly and skips everything below it.
   2. The default skin (mount + the per-kind binders): renders the declared controls and
      views with the kit's classes. Replace it freely; the core underneath is the part
      that should never be rewritten per app.

   The kit never holds app state of its own and never templates your markup - generated
   apps stay readable and diffable. */

const PUSH_DEBOUNCE_MS = 60;

/* --- Headless core ----------------------------------------------------------------- */

/* Open the live connection for this app. Returns the app object synchronously; frames
   and status changes arrive on the callbacks and on any later .onState/.onStatus
   subscribers. Options: onState(state), onStatus({kind, text}), share (default true -
   one SSE stream shared across this app's tabs via Web Locks + BroadcastChannel). */
export function connect(opts = {}) {
  const segs = location.pathname.split("/").filter(Boolean);
  const homeId = segs[1] || "";
  // The entry link carries ?token= once; the server answered it with a session cookie,
  // so the URL drops the token immediately (nothing secret in the location bar).
  if (new URLSearchParams(location.search).get("token")) {
    history.replaceState(null, "", location.pathname);
  }
  const api = (action) => `/app/${homeId}/api/${action}`;

  const stateSubs = new Set();
  const statusSubs = new Set();
  const editing = new Set();
  const pending = new Map(); // id -> {timer, resolvers}
  let lastStatus = null;
  let shut = false; // app.close() called - the page is done, nothing recovers

  const app = {
    homeId,
    state: null,

    onState(fn) { stateSubs.add(fn); if (app.state) fn(app.state); return () => stateSubs.delete(fn); },
    onStatus(fn) { statusSubs.add(fn); if (lastStatus) fn(lastStatus); return () => statusSubs.delete(fn); },

    control(id) {
      return (app.state && app.state.controls || []).find((c) => c.id === id) || null;
    },

    /* Echo suppression is the binder's call: mark a control while the user holds it and
       state frames stop overwriting that widget (the canvas mirrors the drag anyway). */
    editing(id, active) { if (active) editing.add(id); else editing.delete(id); },
    isEditing(id) { return editing.has(id); },
    /* Is any control being held right now? The viewport asks before it re-frames: a
       camera that moves under a drag reads as the model changing size. */
    editingAny() { return editing.size > 0; },

    /* Explicit gesture boundary, sent to the server: every push between "start" and "end"
       amends ONE undo record however slowly the drag moved (a time gap alone cannot tell
       a slow drag from two separate edits). Fire-and-forget - a lost marker degrades to
       the server's gap fallback, never breaks a push. The drag's FINAL push should carry
       {gesture: "end"} instead of a separate marker so the value and the close cannot
       race each other. */
    gesture(id, kind) {
      fetch(api("values"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id, gesture: kind }),
      }).catch(() => { /* the push path reports connectivity honestly */ });
    },

    /* Push a value. Continuous gestures use the default debounce (per control, trailing);
       discrete commits pass {immediate: true}; a drag's final push passes {gesture: "end"}
       (implies immediate) to close its undo bracket atomically. Resolves {ok, status,
       control?, clamped?, error?, code?, superseded?}; refusals also surface through
       onStatus so a page that ignores the promise still tells the truth. */
    push(id, value, { immediate = false, gesture = null } = {}) {
      return new Promise((resolve) => {
        let entry = pending.get(id);
        if (!entry) { entry = { timer: 0, resolvers: [] }; pending.set(id, entry); }
        else if (entry.timer) { clearTimeout(entry.timer); }
        // A superseded call resolved honestly: its value never went out.
        entry.resolvers.forEach((r) => r({ ok: true, superseded: true }));
        entry.resolvers = [resolve];
        entry.value = value;
        entry.gesture = gesture || entry.gesture || null;
        const fire = () => {
          const resolvers = entry.resolvers;
          const v = entry.value;
          const g = entry.gesture;
          pending.delete(id);
          doPush(id, v, g).then((r) => resolvers.forEach((res) => res(r)));
        };
        if (immediate || gesture) fire();
        else entry.timer = setTimeout(fire, PUSH_DEBOUNCE_MS);
      });
    },

    close() {
      shut = true;
      stopRecovery();
      stream.close();
    },
  };

  async function doPush(id, value, gesture) {
    let resp;
    try {
      resp = await fetch(api("values"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(gesture ? { id, value, gesture } : { id, value }),
      });
    } catch (e) {
      setStatus({ kind: "disconnected", text: "push failed — is Rhino still open?" });
      return { ok: false, status: 0, error: String(e) };
    }
    if (resp.ok) {
      const receipt = await resp.json().catch(() => ({}));
      return { ok: true, status: resp.status, control: receipt.control, clamped: !!receipt.clamped };
    }
    const body = await resp.json().catch(() => ({}));
    const text = plain(body.error || `push refused (${resp.status})`);
    // A refusal names the control it came from (id) so a skin can mark that control and
    // snap it back - the change did not land, and the pill alone was not enough of a
    // disclosure (round-7 finding 1). Classified by the body's CODE: the front-tab refusal
    // gets the kit's own copy, a closed definition the closed line plus the recovery poll
    // (round-10 S10.3: a raw 409 sentence used to land in the pill as "blocked").
    if (resp.status === 409 && body.code === "WIREIFY_DOC_NOT_ACTIVE") setStatus({ kind: "background", text: REFUSED_TEXT, id });
    else if (resp.status === 409 && body.code === "WIREIFY_DOC_NOT_OPEN") { setStatus({ kind: "closed", text: CLOSED_TEXT, id }); startRecovery(); }
    else if (resp.status === 409) setStatus({ kind: "blocked", text, id });
    else if (resp.status === 401) setStatus({ kind: "disconnected", text: EXPIRED_TEXT });
    else if (resp.status === 503) setStatus({ kind: "stale", text });
    else setStatus({ kind: "error", text, id });
    return { ok: false, status: resp.status, error: text, code: body.code };
  }

  function setStatus(status) {
    lastStatus = status;
    statusSubs.forEach((fn) => fn(status));
    if (opts.onStatus) opts.onStatus(status);
  }

  function applyState(state) {
    clearTimeout(solveTimer);
    app.state = state;
    stateSubs.forEach((fn) => fn(state));
    if (opts.onState) opts.onState(state);
    // The front-tab rule, said BEFORE any drag: Grasshopper solves only the front
    // document, so a value pushed at a background tab would sit unsolved with its outputs
    // empty - the server refuses instead (round 7 exempted this path for one build and
    // measured exactly that: pushes landed, nothing downstream solved, the pill read
    // live). Reads and this stream keep flowing from the background; closed / expired /
    // unreachable stay the recovery classifier's.
    if (state.isActiveCanvas === false) setStatus({ kind: "background", text: NOT_FRONT_TEXT });
    else setStatus({ kind: "live", text: "live" });
  }

  /* A solving frame arrives at SolutionStart; the matching state frame lands at
     SolutionEnd and cancels it. Flip the pill to stale only when the solve outlives the
     delay - a fast solve should never flicker it. */
  let solveTimer = 0;
  function applyStatusFrame(frame) {
    if (frame.solving) {
      clearTimeout(solveTimer);
      solveTimer = setTimeout(() => setStatus({ kind: "stale", text: "solving…" }), 250);
      return;
    }
    const text = frame.error || "status";
    // Classified by the frame's CODE, never by its wording, and never rendering a protocol
    // sentence raw: the initial frame of every EventSource reconnect used to carry the
    // agent's WIREIFY_DOC_NOT_OPEN recovery sentence into the user's pill on the way to the
    // closed line (round-10 S10.3). A codeless frame with "closed" in it is an older server.
    const code = frame.code || "";
    if (code === "WIREIFY_DOC_NOT_OPEN" || (!code && text.indexOf("closed") >= 0)) {
      // Say the recovery AND do it: the copy alone left users staring at a dead page
      // (round-5 S5.11c, reproduced three times) while the server answered correctly the
      // moment the definition was back. The recovery poll heals the page on its own; the
      // sentence is the fallback for the window in between.
      setStatus({ kind: "closed", text: CLOSED_TEXT });
      startRecovery();
    } else {
      setStatus({ kind: "stale", text: plain(text) });
    }
  }

  /* A server sentence for the pill: the app surface writes page copy, and any protocol
     prefix that still leads a message (WIREIFY_…: ) is dropped - the code is switched on,
     never shown. */
  const plain = (text) => String(text || "").replace(/^WIREIFY_[A-Z_]+:\s*/, "");

  const CLOSED_TEXT = "definition closed — reopen it in Rhino; this page reconnects on its own";
  const NOT_FRONT_TEXT = "not in front — bring the definition's Grasshopper tab to front to interact";
  const REFUSED_TEXT = "change not applied — the definition is not in front; bring its Grasshopper tab to front and try again";
  const EXPIRED_TEXT = "this app link has expired — app links rotate every Rhino run; "
    + "press Open app on the Wireify component in Grasshopper (or ask Claude for get_app_info) and open the fresh link";

  /* Recovery: a cheap state GET every few seconds, SWITCHED ON THE STATUS it answers.
     The server distinguishes healthy (200), definition closed (409), manifest missing or
     unparseable (404, the reason in the body), and a rotated link (401) — and rendering
     three of those as "is Rhino open?" sent users to restart Rhino over a JSON comma
     (round-6 S6.11k; the cold-load half is S6.11d). Closed and manifest faults keep
     polling (they heal on their own); a rotated link never can, so that message stands
     and the poll stops. Only a fetch that cannot reach the server at all earns the
     generic disconnected line - which is then true. */
  let recovering = 0;
  async function probe() {
    let resp;
    try { resp = await fetch(api("state")); }
    catch {
      setStatus({ kind: "disconnected", text: "disconnected — is Rhino (and the definition) still open?" });
      return;
    }
    if (resp.ok) {
      let state;
      try { state = await resp.json(); } catch { return; }
      stopRecovery();
      applyState(state);
      stream.close();
      stream = openAppStream();
      return;
    }
    if (resp.status === 401) {
      // WIREIFY_APP_UNAUTHORIZED: the token rotated with the Rhino run - nothing heals it.
      stopRecovery();
      setStatus({ kind: "disconnected", text: EXPIRED_TEXT });
      return;
    }
    let body = null;
    try { body = await resp.json(); } catch { /* non-JSON body */ }
    if (resp.status === 409) {
      // Switched on the CODE, not the status: only a definition that is not open earns the
      // closed line (it heals on its own when the file is back). Any other refusal shows the
      // server's own sentence — round 9 (S9.47) rendered an open definition as "closed"
      // because every 409 landed here.
      const code = body && body.code;
      if (!code || code === "WIREIFY_DOC_NOT_OPEN") setStatus({ kind: "closed", text: CLOSED_TEXT });
      else setStatus({ kind: "blocked", text: plain((body && body.error) || `state refused (${code})`) });
      return;
    }
    setStatus({ kind: "stale", text: plain((body && body.error) || `state check failed (${resp.status})`) });
  }
  function startRecovery() {
    if (shut || recovering) return;
    stream.close(); // stop the dead stream's own retries; probe() reopens on 200
    probe();
    recovering = setInterval(probe, 5000);
  }
  function stopRecovery() {
    clearInterval(recovering);
    recovering = 0;
  }

  const streamHandlers = {
    state: applyState,
    status: applyStatusFrame,
    // Any stream death routes through the classifier - including a COLD LOAD into a
    // broken manifest or an expired link, where the very first connect is what fails
    // and no status frame ever arrives to explain it.
    dropped: () => startRecovery(),
  };
  const openAppStream = () => openStream(homeId, api("events"), streamHandlers, opts.share !== false);
  let stream = openAppStream();

  return app;
}

/* One SSE stream per app, shared across tabs when the platform allows: the tab holding
   the Web Lock owns the EventSource and rebroadcasts frames on a BroadcastChannel;
   when it closes, the lock (and the stream) pass to the next tab. Browsers cap
   connections per origin, so parked tabs must not each hold one open. */
function openStream(homeId, url, handlers, share) {
  const direct = (h) => {
    const es = new EventSource(url);
    es.addEventListener("state", (e) => h.state(JSON.parse(e.data)));
    es.addEventListener("status", (e) => h.status(JSON.parse(e.data)));
    es.onerror = () => h.dropped();
    return { close: () => es.close() };
  };

  if (!share || typeof BroadcastChannel === "undefined" || !("locks" in navigator)) {
    return direct(handlers);
  }

  const name = "wireify-app-" + homeId;
  const bc = new BroadcastChannel(name);
  let leader = false;
  let inner = null;
  let last = null;
  let release = null; // resolves the lock-holding promise, so close() really releases

  bc.onmessage = (e) => {
    const m = e.data || {};
    if (leader) {
      if (m.t === "hello" && last) bc.postMessage({ t: "state", d: last });
      return;
    }
    if (m.t === "state") handlers.state(m.d);
    else if (m.t === "status") handlers.status(m.d);
    else if (m.t === "dropped") handlers.dropped();
  };
  bc.postMessage({ t: "hello" });

  navigator.locks.request(name, () => {
    leader = true;
    inner = direct({
      state: (d) => { last = d; handlers.state(d); bc.postMessage({ t: "state", d }); },
      status: (d) => { handlers.status(d); bc.postMessage({ t: "status", d }); },
      dropped: () => { handlers.dropped(); bc.postMessage({ t: "dropped" }); },
    });
    // Held until close() or tab death — an explicit release matters: the closed-state
    // reconnect opens a FRESH stream in this same tab, and a lock held by the dead one
    // would starve it forever.
    return new Promise((resolve) => { release = resolve; });
  });

  return {
    close() {
      if (inner) inner.close();
      bc.close();
      if (release) release();
    },
  };
}

/* --- Formatting -------------------------------------------------------------------- */

export const fmt = {
  num(value, decimals) {
    const d = typeof decimals === "number" ? Math.max(0, Math.min(12, decimals)) : 3;
    return Number(value).toFixed(d);
  },
  count(n) { return Number(n).toLocaleString("en-US"); },
};

/* --- Theme ------------------------------------------------------------------------- */
/* The pre-paint half (reading ?theme= / localStorage before first paint) is an inline
   script in the page head - it cannot live in a module. This is the toggle half. */

export const theme = {
  bindToggle(el) {
    const resolved = () => {
      const mode = document.documentElement.getAttribute("data-theme") || "auto";
      if (mode !== "auto") return mode;
      return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    };
    const label = () => { el.textContent = resolved() === "dark" ? "Light" : "Dark"; };
    el.addEventListener("click", () => {
      const next = resolved() === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-theme", next);
      try { localStorage.setItem("ify-theme", next); } catch { /* private mode */ }
      label();
    });
    label();
  },
};

/* --- Charts (hand-rolled SVG on tokens; single series until the mark ramp lands) --- */

const SVG_NS = "http://www.w3.org/2000/svg";

function chartSvg(el, width, height) {
  el.classList.add("ify-chart");
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  el.replaceChildren(svg);
  return svg;
}

export const chart = {
  bars(el, values, { height = 120 } = {}) {
    const width = 320;
    const pad = { top: 6, right: 4, bottom: 14, left: 30 };
    const svg = chartSvg(el, width, height);
    const max = Math.max(...values, 0) || 1;
    const iw = width - pad.left - pad.right;
    const ih = height - pad.top - pad.bottom;
    for (const f of [0, 0.5, 1]) {
      const y = pad.top + ih - f * ih;
      const line = document.createElementNS(SVG_NS, "line");
      line.setAttribute("x1", pad.left); line.setAttribute("x2", width - pad.right);
      line.setAttribute("y1", y); line.setAttribute("y2", y);
      line.setAttribute("class", "ify-chart__grid");
      svg.append(line);
      const t = document.createElementNS(SVG_NS, "text");
      t.setAttribute("x", pad.left - 4); t.setAttribute("y", y + 3);
      t.setAttribute("text-anchor", "end");
      t.textContent = fmt.num(f * max, max >= 10 ? 0 : 1);
      svg.append(t);
    }
    const step = iw / values.length;
    values.forEach((v, i) => {
      const h = (v / max) * ih;
      const r = document.createElementNS(SVG_NS, "rect");
      r.setAttribute("x", pad.left + i * step + step * 0.15);
      r.setAttribute("y", pad.top + ih - h);
      r.setAttribute("width", step * 0.7);
      r.setAttribute("height", h);
      r.setAttribute("class", "ify-chart__mark");
      svg.append(r);
    });
    return svg;
  },

  line(el, values, { height = 120 } = {}) {
    const width = 320;
    const pad = { top: 6, right: 4, bottom: 14, left: 30 };
    const svg = chartSvg(el, width, height);
    const max = Math.max(...values, 0) || 1;
    const min = Math.min(...values, 0);
    const iw = width - pad.left - pad.right;
    const ih = height - pad.top - pad.bottom;
    const y = (v) => pad.top + ih - ((v - min) / (max - min || 1)) * ih;
    for (const f of [0, 1]) {
      const gy = pad.top + ih - f * ih;
      const line = document.createElementNS(SVG_NS, "line");
      line.setAttribute("x1", pad.left); line.setAttribute("x2", width - pad.right);
      line.setAttribute("y1", gy); line.setAttribute("y2", gy);
      line.setAttribute("class", "ify-chart__grid");
      svg.append(line);
      const t = document.createElementNS(SVG_NS, "text");
      t.setAttribute("x", pad.left - 4); t.setAttribute("y", gy + 3);
      t.setAttribute("text-anchor", "end");
      t.textContent = fmt.num(min + f * (max - min), 1);
      svg.append(t);
    }
    const pts = values.map((v, i) =>
      `${pad.left + (i / (values.length - 1 || 1)) * iw},${y(v)}`).join(" ");
    const poly = document.createElementNS(SVG_NS, "polyline");
    poly.setAttribute("points", pts);
    poly.setAttribute("class", "ify-chart__stroke");
    svg.append(poly);
    return svg;
  },
};

/* --- Default skin: binders --------------------------------------------------------- */
/* Every binder derives bounds/items/step from the live state frame, never from markup
   (derive, don't restate - a canvas edit re-shapes the app on the next frame). Each
   returns an update(controlState) the renderer calls per frame. */

/* An integer-rounded slider (step 1, or 2 for even/odd) reads as an integer, as it does
   on the canvas; its decimals setting only shapes float rounding. */
const shownDecimals = (s) => (Number.isInteger(s.step) && s.step >= 1 ? 0 : s.decimals);

export const bind = {
  slider(app, id, input, valueEl) {
    input.addEventListener("pointerdown", () => { app.editing(id, true); app.gesture(id, "start"); });
    input.addEventListener("pointerup", () => {
      app.editing(id, false);
      app.push(id, Number(input.value), { gesture: "end" });
    });
    input.addEventListener("input", () => {
      if (valueEl) valueEl.textContent = input.value;
      app.push(id, Number(input.value));
    });
    return (s) => {
      input.min = s.min; input.max = s.max; input.step = s.step || "any";
      if (!app.isEditing(id)) {
        input.value = s.value;
        // An expression slider reports both truths: the raw value the thumb holds and
        // the evaluated number feeding the canvas.
        const d = shownDecimals(s);
        if (valueEl) valueEl.textContent = s.evaluated == null
          ? fmt.num(s.value, d)
          : `${fmt.num(s.value, d)} = ${fmt.num(s.evaluated, d)}`;
      }
    };
  },

  toggle(app, id, input, valueEl) {
    input.addEventListener("change", () => app.push(id, input.checked, { immediate: true }));
    return (s) => {
      input.checked = !!s.bool;
      if (valueEl) valueEl.textContent = s.bool ? "true" : "false";
    };
  },

  select(app, id, select, valueEl) {
    select.addEventListener("change", () => app.push(id, Number(select.value), { immediate: true }));
    return (s) => {
      const items = s.items || [];
      if (select.options.length !== items.length
          || items.some((it, i) => select.options[i].textContent !== it)) {
        select.replaceChildren(...items.map((it, i) => {
          const o = document.createElement("option");
          o.value = String(i); o.textContent = it;
          return o;
        }));
      }
      if (s.selected != null) select.value = String(s.selected);
      if (valueEl) valueEl.textContent = s.text || "";
    };
  },

  text(app, id, area, valueEl) {
    area.addEventListener("focus", () => app.editing(id, true));
    area.addEventListener("blur", () => app.editing(id, false));
    area.addEventListener("change", () => app.push(id, area.value, { immediate: true }));
    return (s) => {
      if (!app.isEditing(id)) area.value = s.text || "";
      if (valueEl) valueEl.textContent = `${(s.text || "").length} chars`;
    };
  },

  hold(app, id, btn) {
    const release = () => {
      if (btn.dataset.pressed === "true") {
        btn.dataset.pressed = "false";
        app.push(id, false, { immediate: true });
      }
    };
    btn.addEventListener("pointerdown", () => {
      btn.dataset.pressed = "true";
      app.push(id, true, { immediate: true });
    });
    btn.addEventListener("pointerup", release);
    btn.addEventListener("pointerleave", release);
    return (s) => { btn.dataset.pressed = s.bool ? "true" : "false"; };
  },

  /* MD slider: axes[0]/axes[1] on the pad; a third axis, when the canvas slider is 3D,
     rides a separate range input. Pushes speak the comma-separated axis string. */
  pad(app, id, padEl, thumbEl, valueEl, zInput) {
    let axes = null;
    const send = (opts) => {
      if (!axes) return;
      const parts = axes.map((a) => a.value);
      app.push(id, parts.map((v) => String(v)).join(","), opts);
    };
    const fromEvent = (e) => {
      if (!axes) return;
      const rect = padEl.getBoundingClientRect();
      const fx = Math.min(Math.max((e.clientX - rect.left) / rect.width, 0), 1);
      const fy = 1 - Math.min(Math.max((e.clientY - rect.top) / rect.height, 0), 1);
      axes[0] = { ...axes[0], value: axes[0].min + fx * (axes[0].max - axes[0].min) };
      axes[1] = { ...axes[1], value: axes[1].min + fy * (axes[1].max - axes[1].min) };
      paint();
      send();
    };
    let down = false;
    padEl.addEventListener("pointerdown", (e) => {
      down = true;
      app.editing(id, true);
      app.gesture(id, "start");
      padEl.setPointerCapture(e.pointerId);
      fromEvent(e);
    });
    padEl.addEventListener("pointermove", (e) => { if (down) fromEvent(e); });
    padEl.addEventListener("pointerup", () => {
      down = false;
      app.editing(id, false);
      send({ gesture: "end" });
    });
    if (zInput) {
      zInput.addEventListener("pointerdown", () => { app.editing(id, true); app.gesture(id, "start"); });
      zInput.addEventListener("pointerup", () => { app.editing(id, false); send({ gesture: "end" }); });
      zInput.addEventListener("input", () => {
        if (!axes || axes.length < 3) return;
        axes[2] = { ...axes[2], value: Number(zInput.value) };
        send();
      });
    }
    const paint = () => {
      if (!axes) return;
      const fx = (axes[0].value - axes[0].min) / ((axes[0].max - axes[0].min) || 1);
      const fy = (axes[1].value - axes[1].min) / ((axes[1].max - axes[1].min) || 1);
      thumbEl.style.left = `${fx * 100}%`;
      thumbEl.style.bottom = `${fy * 100}%`;
      if (valueEl) valueEl.textContent = axes.map((a) => fmt.num(a.value, 3)).join(" · ");
    };
    return (s) => {
      if (app.isEditing(id)) return;
      axes = (s.axes || []).map((a) => ({ ...a }));
      if (zInput && axes.length > 2) {
        zInput.min = axes[2].min; zInput.max = axes[2].max; zInput.step = "any";
        zInput.value = axes[2].value;
      }
      paint();
    };
  },

  color(app, id, input, hexEl) {
    input.addEventListener("change", () => app.push(id, input.value, { immediate: true }));
    return (s) => {
      const hex = s.colour || "#000000";
      // A native color input holds no alpha - feed it the rgb 6 digits, report the truth
      // (alpha included) beside it.
      input.value = hex.length === 9 ? hex.slice(0, 7) : hex;
      if (hexEl) hexEl.textContent = hex;
    };
  },
};

/* --- Default skin: renderer -------------------------------------------------------- */
/* Renders the declared controls and views into a page skeleton, reconciles rows whose
   control left the frame, and keeps the status pill honest. Expects (or creates) three
   slots inside root: [data-ify-status], [data-ify-controls], [data-ify-views]. */

export function mount(app, root = document) {
  const statusEl = root.querySelector("[data-ify-status]");
  const controlsEl = root.querySelector("[data-ify-controls]");
  const viewsEl = root.querySelector("[data-ify-views]");
  const widgets = new Map();

  const PILL = { live: "live", stale: "stale", blocked: "blocked", background: "blocked", closed: "down", disconnected: "down", error: "stale" };
  app.onStatus(({ kind, text, id }) => {
    if (statusEl) {
      statusEl.className = "ify-status ify-status--" + (PILL[kind] || "stale");
      // A live status shows its text when the app supplied one (a standalone page labels
      // itself); the kit's own live status carries the word "live".
      statusEl.replaceChildren(dotSpan(), document.createTextNode(kind === "live" && !text ? "live" : text));
      statusEl.title = text;
    }
    // A refused push marks the control it came from and snaps it back to the canvas value:
    // the change did not land, and the row says so - not just the pill (round-7 finding 1).
    if (id && widgets.has(id))
      markRefused(id, kind === "background" ? "not applied — bring the definition to front" : "not applied");
  });

  app.onState((state) => {
    renderWarnings(state.warnings || []);
    if (controlsEl) renderControls(state.controls || []);
    if (viewsEl) renderViews(state.views || []);
    if (state.isActiveCanvas !== false) clearRefused();
  });

  function markRefused(id, note) {
    const w = widgets.get(id);
    w.root.classList.add("ify-refused");
    const host = w.root.querySelector(":scope > .ify-slider__text") || w.root;
    let line = host.querySelector(":scope > .ify-refused__note");
    if (!line) {
      line = document.createElement("span");
      line.className = "ify-refused__note";
      host.append(line);
    }
    line.textContent = note;
    const current = app.control(id);
    if (current) w.update(current);
  }

  function clearRefused() {
    for (const w of widgets.values()) {
      if (!w.root.classList.contains("ify-refused")) continue;
      w.root.classList.remove("ify-refused");
      const line = w.root.querySelector(".ify-refused__note");
      if (line) line.remove();
    }
  }

  /* Manifest declarations the server could not use arrive as frame warnings - render
     them, because a silently dropped view reads as a plugin bug to the page author who
     just declared it (round-6 S6.11j cost a fresh agent its session). The strip leads the
     controls section (or views, on a controls-less page); a custom-layout page that mounts
     for the pill alone gets it at the top of the page - the one place a page cannot lose
     it (round-7 finding 3: no slot meant no strip, silently). */
  function renderWarnings(warnings) {
    const slot = controlsEl || viewsEl || root.body || root;
    if (!slot) return;
    let el = slot.querySelector(":scope > .ify-warnings");
    if (!warnings.length) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement("div");
      el.className = "ify-warnings";
      slot.prepend(el);
    }
    el.replaceChildren(...warnings.map((w) => {
      const line = document.createElement("p");
      line.className = "ify-warnings__line";
      line.textContent = "manifest: " + w;
      return line;
    }));
  }

  function dotSpan() {
    const dot = document.createElement("span");
    dot.className = "ify-status__dot";
    return dot;
  }

  function renderControls(controls) {
    const seen = new Set();
    for (const c of controls) {
      seen.add(c.id);
      let w = widgets.get(c.id);
      if (!w || w.kind !== c.kind) {
        if (w) w.root.remove();
        w = build(c);
        widgets.set(c.id, w);
      }
      w.update(c);
      renderLabel(w.root, c);
    }
    for (const [id, w] of widgets) {
      if (!seen.has(id)) { w.root.remove(); widgets.delete(id); }
    }
    showEmpty(controlsEl, controls.length === 0,
      "No controls declared yet — ask Claude to stage inputs, or add them to app/manifest.json.");
  }

  /* Honesty in pixels: an empty section says so instead of rendering headings over a
     void - the state every fresh home starts in. */
  function showEmpty(slot, empty, text) {
    let el = slot.querySelector(":scope > .ify-empty");
    if (!empty) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement("p");
      el.className = "ify-empty";
      slot.append(el);
    }
    el.textContent = text;
  }

  /* A control's name as the page shows it: the manifest's `label` when the home declares
     one, the canvas nickname otherwise. A labelled control keeps its nickname beside the
     label in parentheses — the raw name is the handle a Grasshopper user maps sliders by —
     and the manifest's `help` is the one line under it. Re-rendered every frame: the
     manifest watcher can change both while the page is open. A missing control has no
     nickname left to show: its id is the one handle the author can match against the
     manifest (round-7 finding 9). */
  function renderLabel(rowEl, c, fallback) {
    const textEl = rowEl.querySelector(":scope > .ify-slider__text");
    if (!textEl) return;
    let name = textEl.querySelector(":scope > .ify-slider__name");
    if (!name) {
      name = document.createElement("span");
      name.className = "ify-slider__name";
      textEl.prepend(name);
    }
    name.textContent = c.label || c.nickName || (c.kind === "missing" ? c.id : (fallback || c.kind));

    // The meta line: the canvas nickname beside the range, both in the label face. Two
    // stacked lines cost a control its height for no reading (Hossein, 2026-09-15).
    let meta = textEl.querySelector(":scope > .ify-slider__meta");
    if (!meta) {
      meta = document.createElement("span");
      meta.className = "ify-slider__meta";
      textEl.append(meta);
    }
    let nick = meta.querySelector(":scope > .ify-slider__nick");
    if (c.label && c.nickName) {
      if (!nick) {
        nick = document.createElement("span");
        nick.className = "ify-slider__nick";
        meta.prepend(nick);
      }
      nick.textContent = "(" + c.nickName + ")";
    } else if (nick) {
      nick.remove();
    }

    // The help line is a row of its own across the whole grid, so a sentence written as
    // one line renders as one line instead of wrapping in a narrow label column.
    let help = rowEl.querySelector(":scope > .ify-slider__help");
    if (c.help) {
      if (!help) {
        help = document.createElement("span");
        help.className = "ify-slider__help";
      }
      help.textContent = c.help;
      if (rowEl.lastElementChild !== help) rowEl.append(help);
    } else if (help) {
      help.remove();
    }
  }

  function row(c, wide) {
    const el = document.createElement(wide ? "div" : "label");
    el.className = wide ? "ify-controls__wide" : "ify-slider";
    if (!wide) {
      const text = document.createElement("span");
      text.className = "ify-slider__text";
      el.append(text);
      renderLabel(el, c);
    }
    controlsEl.append(el);
    return el;
  }

  function build(c) {
    const id = c.id;
    let update;
    let el;
    switch (c.kind) {
      case "slider":
      case "knob": {
        el = row(c);
        const bounds = document.createElement("span");
        bounds.className = "ify-slider__bounds";
        el.querySelector(":scope > .ify-slider__text > .ify-slider__meta").append(bounds);
        const input = document.createElement("input");
        input.type = "range";
        input.className = "ify-slider__input";
        const out = document.createElement("output");
        out.className = "ify-slider__value";
        el.append(input, out);
        const base = bind.slider(app, id, input, out);
        update = (s) => {
          // The range, not the increment: a step reads as a number the user must honour
          // and the input already snaps to it.
          bounds.textContent = `${fmt.num(s.min, shownDecimals(s))} – ${fmt.num(s.max, shownDecimals(s))}`;
          base(s);
        };
        break;
      }
      case "toggle": {
        el = row(c);
        const wrap = document.createElement("span");
        wrap.className = "ify-check";
        const input = document.createElement("input");
        input.type = "checkbox";
        wrap.append(input);
        const out = document.createElement("output");
        out.className = "ify-slider__value";
        el.append(wrap, out);
        update = bind.toggle(app, id, input, out);
        break;
      }
      case "valuelist": {
        el = row(c);
        const wrap = document.createElement("span");
        wrap.className = "ify-select";
        const select = document.createElement("select");
        wrap.append(select);
        const out = document.createElement("output");
        out.className = "ify-slider__value";
        el.append(wrap, out);
        update = bind.select(app, id, select, out);
        break;
      }
      case "panel": {
        el = row(c, true);
        const name = document.createElement("div");
        name.className = "ify-slider__text";
        const area = document.createElement("textarea");
        area.className = "ify-input";
        area.rows = 2;
        // RTL prose (a Persian panel, round-9 S9.42) lays out against its own base direction
        // instead of the page's: alignment and neutral characters resolve from the content.
        area.dir = "auto";
        const out = document.createElement("p");
        out.className = "ify-fine";
        out.dir = "auto";
        el.append(name, area, out);
        renderLabel(el, c, "panel");
        update = bind.text(app, id, area, out);
        break;
      }
      case "button": {
        el = row(c);
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "ify-btn ify-btn--sm ify-hold";
        btn.textContent = "hold";
        const out = document.createElement("output");
        out.className = "ify-slider__value";
        el.append(btn, out);
        const base = bind.hold(app, id, btn);
        update = (s) => { base(s); out.textContent = s.bool ? "pressed" : "idle"; };
        break;
      }
      case "mdslider": {
        el = row(c, true);
        const name = document.createElement("div");
        name.className = "ify-slider__text";
        const pad = document.createElement("div");
        pad.className = "ify-pad";
        const thumb = document.createElement("span");
        thumb.className = "ify-pad__thumb";
        pad.append(thumb);
        const out = document.createElement("p");
        out.className = "ify-pad__value";
        el.append(name, pad, out);
        let z = null;
        if ((c.axes || []).length > 2) {
          z = document.createElement("input");
          z.type = "range";
          z.className = "ify-slider__input";
          el.append(z);
        }
        renderLabel(el, c, "point");
        update = bind.pad(app, id, pad, thumb, out, z);
        break;
      }
      case "colour": {
        el = row(c);
        const wrap = document.createElement("span");
        wrap.className = "ify-color";
        const input = document.createElement("input");
        input.type = "color";
        wrap.append(input);
        const hex = document.createElement("output");
        hex.className = "ify-color__hex";
        el.append(wrap, hex);
        update = bind.color(app, id, input, hex);
        break;
      }
      default: {
        // missing / unsupported:<name> - render the truth, never a dead fake control.
        // "missing" is a first-class kind: the declared param left the document (undone
        // away, deleted) and may come back on the next frame.
        el = row(c);
        const out = document.createElement("span");
        out.className = "ify-fine";
        el.append(document.createElement("span"), out);
        update = (s) => {
          out.textContent = s.kind === "missing"
            ? `removed from canvas — ctrl-Z in Grasshopper restores it (id ${s.id})`
            : s.kind;
        };
      }
    }
    return { kind: c.kind, root: el, update };
  }

  function renderViews(views) {
    if (views.length === 0) {
      viewsEl.replaceChildren();
      showEmpty(viewsEl, true,
        "No views declared yet — ask Claude which outputs to watch (views are a conversation, never auto-picked).");
      return;
    }
    viewsEl.replaceChildren(...views.map((v) => {
      const fig = document.createElement("figure");
      fig.className = "ify-panel";
      const meta = document.createElement("figcaption");
      meta.className = "ify-panel__meta";
      // Truncation labeled, per the honesty rules: samples are a sample whenever the
      // frame says so (raise the view's manifest "samples" to deliver more).
      meta.textContent = `${fmt.count(v.tree.pathCount)} branch(es) · ${fmt.count(v.tree.dataCount)} item(s)`
        + (v.samplesTruncated ? ` · showing ${fmt.count((v.samples || []).length)}` : "");
      const title = document.createElement("div");
      title.className = "ify-kicker";
      // label = the qualified display string; param = the bare name a page matches on.
      title.textContent = v.label || v.param;
      const pre = document.createElement("pre");
      pre.textContent = (v.samples || []).map((s) => s.value).join("\n") || "(no samples)";
      fig.append(meta, title, pre);
      const warn = (v.warnings || []).join("; ");
      if (warn) {
        const fine = document.createElement("p");
        fine.className = "ify-fine";
        fine.textContent = warn;
        fig.append(fine);
      }
      return fig;
    }));
  }

  return { widgets };
}

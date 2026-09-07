(function () {
  "use strict";
  var DATA = window.CARD_DATA;
  if (!DATA) {
    document.body.innerHTML =
      '<p style="padding:24px">Could not load <code>data.js</code>. ' +
      'Run <code>python tooling/card-admin/build_data.py</code> and open this file beside the generated data.</p>';
    return;
  }

  var TYPE_COLORS = { MINION: "#e0803a", SPELL: "#4a90d9", WEAPON: "#8e6fd0", HERO_POWER: "#4bbf8a" };

  // Rarity is an ORDERED ladder, so it is always drawn low-to-high in this
  // order and never sorted by count — position is half the encoding.
  var RARITY_ORDER = ["COMMON", "RARE", "EPIC", "LEGENDARY"];
  // Lifted verbatim from the game's own --rarity-* tokens in
  // unity/Emberfall/Assets/UI/Deckbuilder/theme.uss, so a bar here reads as the
  // same rarity a player sees on the frame. That match is worth one knowing
  // deviation: RARE-blue and EPIC-purple collapse to ~ΔE 3 under deuteranopia,
  // which is why nothing in this panel encodes rarity by color ALONE — every
  // segment carries its name and count in the labelled row beneath it, and the
  // order above is fixed. Re-check with the dataviz palette validator if these
  // ever stop mirroring theme.uss.
  var RARITY_COLORS = {
    COMMON: "#9aa7b6",
    RARE: "#3d7bf5",
    EPIC: "#a24be5",
    LEGENDARY: "#f0a63c",
  };
  var OTHER_COLOR = "#5c6472";

  var el = {
    source: document.getElementById("source-note"),
    fClass: document.getElementById("f-class"),
    fCostMin: document.getElementById("f-cost-min"),
    fCostMax: document.getElementById("f-cost-max"),
    fType: document.getElementById("f-type"),
    fRarity: document.getElementById("f-rarity"),
    fKeyword: document.getElementById("f-keyword"),
    fTribe: document.getElementById("f-tribe"),
    fReset: document.getElementById("f-reset"),
    scopeCount: document.getElementById("scope-count"),
    scopeDesc: document.getElementById("scope-desc"),
    typeSplit: document.getElementById("type-split"),
    raritySplit: document.getElementById("rarity-split"),
    rarityMatrix: document.getElementById("rarity-matrix"),
    rarityMatrixHead: document.getElementById("rarity-matrix-head"),
    rarityMode: document.getElementById("rarity-mode"),
    keywordBars: document.getElementById("keyword-bars"),
    selectorBars: document.getElementById("selector-bars"),
    tribeBars: document.getElementById("tribe-bars"),
    actionBars: document.getElementById("action-bars"),
    costHist: document.getElementById("cost-hist"),
    attackHist: document.getElementById("attack-hist"),
    healthHist: document.getElementById("health-hist"),
  };

  // "count" draws every class against the largest class, so row lengths compare
  // absolute size; "share" normalises each row to 100% so mixes compare.
  var rarityMode = "count";

  el.source.textContent =
    DATA.totalCards + " cards · " + DATA.classes.length + " classes · from " +
    (DATA.generatedFrom || []).map(function (p) { return p.split("/").pop(); }).join(", ");

  // ----- Build filter controls from the corpus -----
  // Class: overview + one option per class.
  var opt = document.createElement("option");
  opt.value = "__ALL__";
  opt.textContent = "All classes (overview)";
  el.fClass.appendChild(opt);
  DATA.classes.forEach(function (c) {
    var o = document.createElement("option");
    o.value = c; o.textContent = c;
    el.fClass.appendChild(o);
  });

  DATA.cardTypes.forEach(function (t) {
    el.fType.appendChild(makeCheck("type", t, t));
  });

  // Rarity checkboxes follow the ladder, not the alphabet.
  var rarities = orderRarities(uniqueSorted(DATA.cards.map(function (c) { return c.rarity; })));
  rarities.forEach(function (r) {
    el.fRarity.appendChild(makeCheck("rarity", r, r));
  });

  var keywords = uniqueSorted(flat(DATA.cards.map(function (c) { return c.keywords; })));
  if (keywords.length === 0) {
    el.fKeyword.innerHTML = '<span class="empty">none</span>';
  } else {
    keywords.forEach(function (k) {
      el.fKeyword.appendChild(makeCheck("keyword", k, k));
    });
  }

  var tribes = uniqueSorted(flat(DATA.cards.map(function (c) { return c.tribe; })));
  if (tribes.length === 0) {
    el.fTribe.innerHTML = '<span class="empty">none</span>';
  } else {
    tribes.forEach(function (t) {
      el.fTribe.appendChild(makeCheck("tribe", t, t));
    });
  }

  // ----- Wire events -----
  [el.fClass, el.fCostMin, el.fCostMax].forEach(function (n) {
    n.addEventListener("input", render);
    n.addEventListener("change", render);
  });
  [el.fType, el.fRarity, el.fKeyword, el.fTribe].forEach(function (container) {
    container.addEventListener("change", render);
  });
  el.fReset.addEventListener("click", function () {
    el.fClass.value = "__ALL__";
    el.fCostMin.value = "";
    el.fCostMax.value = "";
    [el.fType, el.fRarity, el.fKeyword, el.fTribe].forEach(function (container) {
      checkedBoxes(container).forEach(function (b) { b.checked = false; });
    });
    render();
  });
  el.rarityMode.addEventListener("click", function (ev) {
    var btn = ev.target.closest("button[data-mode]");
    if (!btn || btn.dataset.mode === rarityMode) return;
    rarityMode = btn.dataset.mode;
    Array.prototype.forEach.call(el.rarityMode.querySelectorAll("button"), function (b) {
      b.classList.toggle("on", b === btn);
    });
    render();
  });

  render();

  // ---------------------------------------------------------------
  function currentFilter() {
    return {
      cls: el.fClass.value,
      min: parseNum(el.fCostMin.value),
      max: parseNum(el.fCostMax.value),
      types: checkedValues(el.fType),
      rarities: checkedValues(el.fRarity),
      keywords: checkedValues(el.fKeyword),
      tribes: checkedValues(el.fTribe),
    };
  }

  // `ignoreClass` drops only the class predicate, so the per-class matrix can
  // show every class side by side while still honouring the other filters.
  function applyFilter(f, ignoreClass) {
    return DATA.cards.filter(function (c) {
      if (!ignoreClass && f.cls !== "__ALL__" && c["class"] !== f.cls) return false;
      if (f.min !== null && (c.cost === null || c.cost < f.min)) return false;
      if (f.max !== null && (c.cost === null || c.cost > f.max)) return false;
      if (f.types.length && f.types.indexOf(c.type) === -1) return false;
      if (f.rarities.length && f.rarities.indexOf(c.rarity) === -1) return false;
      if (f.keywords.length && !f.keywords.every(function (k) { return c.keywords.indexOf(k) !== -1; })) return false;
      if (f.tribes.length && f.tribes.indexOf(c.tribe) === -1) return false;
      return true;
    });
  }

  function aggregate(cards) {
    var kw = {}, sel = {}, act = {}, cost = {}, type = {}, atk = {}, hp = {}, trb = {}, rar = {};
    cards.forEach(function (c) {
      c.keywords.forEach(function (k) { kw[k] = (kw[k] || 0) + 1; });
      c.selectors.forEach(function (s) { sel[s] = (sel[s] || 0) + 1; });
      c.actionTags.forEach(function (a) { act[a] = (act[a] || 0) + 1; });
      if (c.cost !== null && c.cost !== undefined) cost[c.cost] = (cost[c.cost] || 0) + 1;
      if (c.type) type[c.type] = (type[c.type] || 0) + 1;
      if (c.tribe) trb[c.tribe] = (trb[c.tribe] || 0) + 1;
      rar[c.rarity || "—"] = (rar[c.rarity || "—"] || 0) + 1;
      if (c.type === "MINION") {
        if (c.attack !== null && c.attack !== undefined) atk[c.attack] = (atk[c.attack] || 0) + 1;
        if (c.health !== null && c.health !== undefined) hp[c.health] = (hp[c.health] || 0) + 1;
      }
    });
    return { keywords: kw, selectors: sel, actions: act, cost: cost, type: type, attack: atk, health: hp, tribes: trb, rarity: rar };
  }

  function render() {
    var f = currentFilter();
    var cards = applyFilter(f, false);
    var agg = aggregate(cards);

    el.scopeCount.textContent = cards.length + (cards.length === 1 ? " card" : " cards");
    el.scopeDesc.textContent = describeScope(f);

    renderTypeSplit(agg.type, cards.length);
    renderRaritySplit(agg.rarity, cards.length);
    renderRarityMatrix(f);
    renderBars(el.keywordBars, agg.keywords, "No keywords in this filter.");
    renderBars(el.tribeBars, agg.tribes, "No tribes in this filter.");
    renderBars(el.selectorBars, agg.selectors, "No targeting selectors in this filter.");
    renderBars(el.actionBars, agg.actions, "No tracked actions in this filter.");
    renderHist(el.costHist, agg.cost, cards.length === 0);

    var minionCount = agg.type.MINION || 0;
    renderHist(el.attackHist, agg.attack, minionCount === 0);
    renderHist(el.healthHist, agg.health, minionCount === 0);
  }

  function renderTypeSplit(typeCounts, total) {
    if (total === 0) {
      el.typeSplit.innerHTML = '<div class="empty">No cards match the current filter.</div>';
      return;
    }
    var segs = "";
    var legend = "";
    DATA.cardTypes.forEach(function (t) {
      var n = typeCounts[t] || 0;
      if (n === 0) return;
      var pct = (n / total) * 100;
      var color = TYPE_COLORS[t] || "#888";
      var label = pct >= 8 ? (Math.round(pct) + "%") : "";
      segs += '<div class="split-seg" style="width:' + pct + '%;background:' + color + '" title="' +
        t + ": " + n + ' (' + pct.toFixed(1) + '%)">' + label + "</div>";
      legend += '<div class="item"><span class="dot" style="background:' + color + '"></span>' +
        t + " — " + n + " (" + pct.toFixed(1) + "%)</div>";
    });
    el.typeSplit.innerHTML = '<div class="split-bar">' + segs + "</div>" +
      '<div class="split-legend">' + legend + "</div>";
  }

  // ----- rarity: the active scope -----
  function renderRaritySplit(rarityCounts, total) {
    if (total === 0) {
      el.raritySplit.innerHTML = '<div class="empty">No cards match the current filter.</div>';
      return;
    }
    var segs = "";
    var legend = "";
    presentRarities(rarityCounts).forEach(function (r) {
      var n = rarityCounts[r] || 0;
      if (n === 0) return;
      var pct = (n / total) * 100;
      var color = rarityColor(r);
      var label = pct >= 8 ? (rarityMode === "share" ? pct.toFixed(1) + "%" : String(n)) : "";
      segs += '<div class="split-seg" style="width:' + pct + '%;background:' + color + '" title="' +
        r + ": " + n + " (" + pct.toFixed(1) + '%)">' + label + "</div>";
      legend += '<div class="item"><span class="dot" style="background:' + color + '"></span>' +
        r + " — " + n + " (" + pct.toFixed(1) + "%)</div>";
    });
    el.raritySplit.innerHTML = '<div class="split-bar">' + segs + "</div>" +
      '<div class="split-legend">' + legend + "</div>";
  }

  // ----- rarity: every class side by side -----
  function renderRarityMatrix(f) {
    // Deliberately ignores the class filter — the point of this block is to
    // compare classes, so picking one highlights its row instead of hiding
    // the other eight.
    var pool = applyFilter(f, true);
    el.rarityMatrixHead.textContent = "By class" +
      (hasNonClassFilter(f) ? " — within the current filter" : "") +
      (f.cls === "__ALL__" ? "" : " · " + f.cls + " highlighted");

    var byClass = {};
    DATA.classes.forEach(function (c) { byClass[c] = {}; });
    var loose = {};
    pool.forEach(function (c) {
      var bucket = byClass[c["class"]] || loose;
      var r = c.rarity || "—";
      bucket[r] = (bucket[r] || 0) + 1;
    });

    var totals = aggregate(pool).rarity;
    var order = presentRarities(totals);
    if (order.length === 0) {
      el.rarityMatrix.innerHTML = '<div class="empty">No cards match the current filter.</div>';
      return;
    }

    var classTotals = DATA.classes.map(function (c) { return sum(byClass[c]); });
    var biggest = Math.max.apply(null, classTotals.concat([1]));

    var html = '<div class="rmx-head"><span></span><span></span>' + numsRow(order, null, null, true) + "</div>";
    DATA.classes.forEach(function (cls, i) {
      html += matrixRow(cls, byClass[cls], classTotals[i], biggest, order,
        f.cls !== "__ALL__" && f.cls === cls);
    });
    // The totals row is scaled against itself in both modes: it is the whole
    // pool, so it fills its track and reads as the reference mix. Scaling it
    // against `biggest` like a class row would overflow by ~6x.
    html += matrixRow("All classes", totals, pool.length, pool.length, order, false, true);
    el.rarityMatrix.innerHTML = html;
  }

  function matrixRow(name, counts, total, scaleMax, order, highlight, isTotal) {
    var segs = "";
    order.forEach(function (r) {
      var n = counts[r] || 0;
      if (n === 0) return;
      // share: the row fills its track, so mixes line up across classes.
      // count: the row is scaled against the biggest class, so lengths compare.
      var w = rarityMode === "share"
        ? (total > 0 ? (n / total) * 100 : 0)
        : (scaleMax > 0 ? (n / scaleMax) * 100 : 0);
      segs += '<span class="seg" style="width:' + w + '%;background:' + rarityColor(r) + '" title="' +
        name + " · " + r + ": " + n +
        (total > 0 ? " (" + ((n / total) * 100).toFixed(1) + "% of " + total + ")" : "") + '"></span>';
    });
    return '<div class="rmx-row' + (isTotal ? " total" : "") + '"' +
      (highlight ? ' style="background:rgba(224,128,58,.09);border-radius:6px"' : "") + ">" +
      '<span class="rmx-name">' + name + '<span class="n">' + total + "</span></span>" +
      '<span class="rmx-bar">' + segs + "</span>" +
      numsRow(order, counts, total, false) + "</div>";
  }

  function numsRow(order, counts, total, isHeader) {
    var cells = order.map(function (r) {
      if (isHeader) {
        return '<span class="cell" title="' + r + '"><span class="dot" style="background:' +
          rarityColor(r) + '"></span>' + shortRarity(r) + "</span>";
      }
      var n = counts[r] || 0;
      var text = rarityMode === "share"
        ? (total > 0 ? ((n / total) * 100).toFixed(0) + "%" : "0%")
        : String(n);
      return '<span class="cell' + (n === 0 ? " zero" : "") + '" title="' + r + ": " + n + '">' +
        '<span class="dot" style="background:' + rarityColor(r) + '"></span>' + text + "</span>";
    }).join("");
    return '<span class="rmx-nums">' + cells + "</span>";
  }

  function renderBars(container, counts, emptyMsg) {
    var entries = Object.keys(counts).map(function (k) { return [k, counts[k]]; });
    entries.sort(function (a, b) { return b[1] - a[1] || (a[0] < b[0] ? -1 : 1); });
    if (entries.length === 0) {
      container.innerHTML = '<div class="empty">' + emptyMsg + "</div>";
      return;
    }
    var maxV = entries[0][1];
    container.innerHTML = entries.map(function (e) {
      var w = (e[1] / maxV) * 100;
      return '<div class="bar-row"><span class="lbl" title="' + e[0] + '">' + e[0] + "</span>" +
        '<span class="track"><span class="fill" style="width:' + w + '%"></span></span>' +
        '<span class="val">' + e[1] + "</span></div>";
    }).join("");
  }

  function renderHist(container, counts, isEmpty) {
    var keys = Object.keys(counts).map(Number);
    if (isEmpty || keys.length === 0) {
      container.innerHTML = '<div class="empty">No data for this filter.</div>';
      return;
    }
    var min = Math.min.apply(null, keys);
    var max = Math.max.apply(null, keys);
    var maxV = Math.max.apply(null, keys.map(function (k) { return counts[k]; }));
    var html = "";
    for (var v = min; v <= max; v++) {
      var n = counts[v] || 0;
      var h = maxV > 0 ? (n / maxV) * 100 : 0;
      html += '<div class="col" title="' + v + ": " + n + '">' +
        '<span class="cnt">' + (n || "") + "</span>" +
        '<span class="b" style="height:' + h + '%"></span>' +
        '<span class="ax">' + v + "</span></div>";
    }
    container.innerHTML = html;
  }

  function describeScope(f) {
    var parts = [];
    parts.push(f.cls === "__ALL__" ? "All classes" : f.cls);
    if (f.min !== null || f.max !== null) {
      if (f.min !== null && f.max !== null) parts.push("cost " + f.min + "–" + f.max);
      else if (f.min !== null) parts.push("cost ≥ " + f.min);
      else parts.push("cost ≤ " + f.max);
    }
    if (f.types.length) parts.push(f.types.join("/"));
    if (f.rarities.length) parts.push(f.rarities.join("/"));
    if (f.keywords.length) parts.push("keyword: " + f.keywords.join("+"));
    if (f.tribes.length) parts.push("tribe: " + f.tribes.join("+"));
    return parts.join(" · ");
  }

  function hasNonClassFilter(f) {
    return f.min !== null || f.max !== null || f.types.length > 0 ||
      f.rarities.length > 0 || f.keywords.length > 0 || f.tribes.length > 0;
  }

  // ----- small helpers -----
  function rarityColor(r) { return RARITY_COLORS[r] || OTHER_COLOR; }
  function shortRarity(r) { return r === "LEGENDARY" ? "LEG" : r === "COMMON" ? "COM" : r.slice(0, 4); }

  // The ladder first, then anything the data carries that this panel does not
  // know about — so a new rarity shows up rather than silently vanishing.
  function orderRarities(list) {
    var known = RARITY_ORDER.filter(function (r) { return list.indexOf(r) !== -1; });
    var rest = list.filter(function (r) { return RARITY_ORDER.indexOf(r) === -1; }).sort();
    return known.concat(rest);
  }
  function presentRarities(counts) {
    return orderRarities(Object.keys(counts).filter(function (r) { return counts[r] > 0; }));
  }

  function sum(counts) {
    return Object.keys(counts).reduce(function (a, k) { return a + counts[k]; }, 0);
  }
  function makeCheck(group, value, label) {
    var l = document.createElement("label");
    l.className = "chk";
    var input = document.createElement("input");
    input.type = "checkbox";
    input.value = value;
    input.setAttribute("data-group", group);
    l.appendChild(input);
    l.appendChild(document.createTextNode(label));
    return l;
  }
  function checkedBoxes(container) {
    return Array.prototype.slice.call(container.querySelectorAll('input[type="checkbox"]'))
      .filter(function (b) { return b.checked; });
  }
  function checkedValues(container) { return checkedBoxes(container).map(function (b) { return b.value; }); }
  function parseNum(v) { if (v === "" || v === null || v === undefined) return null; var n = Number(v); return isNaN(n) ? null : n; }
  function uniqueSorted(arr) {
    var seen = {}; var out = [];
    arr.forEach(function (x) { if (x !== null && x !== undefined && !seen[x]) { seen[x] = 1; out.push(x); } });
    return out.sort();
  }
  function flat(arrs) { return Array.prototype.concat.apply([], arrs); }
})();

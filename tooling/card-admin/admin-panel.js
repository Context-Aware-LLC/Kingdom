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

      var el = {
        source: document.getElementById("source-note"),
        fClass: document.getElementById("f-class"),
        fCostMin: document.getElementById("f-cost-min"),
        fCostMax: document.getElementById("f-cost-max"),
        fType: document.getElementById("f-type"),
        fRarity: document.getElementById("f-rarity"),
        fKeyword: document.getElementById("f-keyword"),
        fReset: document.getElementById("f-reset"),
        fTribe: document.getElementById("f-tribe"),
        scopeCount: document.getElementById("scope-count"),
        scopeDesc: document.getElementById("scope-desc"),
        typeSplit: document.getElementById("type-split"),
        keywordBars: document.getElementById("keyword-bars"),
        selectorBars: document.getElementById("selector-bars"),
        costHist: document.getElementById("cost-hist"),
        attackHist: document.getElementById("attack-hist"),
        healthHist: document.getElementById("health-hist"),
        tribeBars: document.getElementById("tribe-bars"),
      };

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

      var tribes = uniqueSorted(flat(DATA.cards.map(function (c) { return c.tribe; })));
      if (tribes.length === 0) {
        el.tribeBars.innerHTML = '<span class="empty">none</span>';
      } else {
        tribes.forEach(function (t) {
          el.fTribe.appendChild(makeCheck("tribe", t, t));
        })
      }

      var rarities = uniqueSorted(DATA.cards.map(function (c) { return c.rarity; }));
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

      // ----- Wire events -----
      [el.fClass, el.fCostMin, el.fCostMax].forEach(function (n) {
        n.addEventListener("input", render);
        n.addEventListener("change", render);
      });
      [el.fType, el.fRarity, el.fKeyword, el.tribeBars].forEach(function (container) {
        container.addEventListener("change", render);
      });
      el.fReset.addEventListener("click", function () {
        el.fClass.value = "__ALL__";
        el.fCostMin.value = "";
        el.fCostMax.value = "";
        checkedBoxes(el.fType).concat(checkedBoxes(el.fRarity)).concat(checkedBoxes(el.fKeyword))
          .forEach(function (b) { b.checked = false; });
        render();
      });

      render();

      // ---------------------------------------------------------------
      function currentFilter() {
        var cls = el.fClass.value;
        var min = parseNum(el.fCostMin.value);
        var max = parseNum(el.fCostMax.value);
        var types = checkedValues(el.fType);
        var rar = checkedValues(el.fRarity);
        var kws = checkedValues(el.fKeyword);
        var trb = checkedValues(el.fTribe);
        return { cls: cls, min: min, max: max, types: types, rarities: rar, keywords: kws, tribes: trb };
      }

      function applyFilter(f) {
        return DATA.cards.filter(function (c) {
          if (f.cls !== "__ALL__" && c["class"] !== f.cls) return false;
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
        var kw = {}, sel = {}, cost = {}, type = {}, atk = {}, hp = {}, trb = {};
        cards.forEach(function (c) {
          c.keywords.forEach(function (k) { kw[k] = (kw[k] || 0) + 1; });
          c.selectors.forEach(function (s) { sel[s] = (sel[s] || 0) + 1; });
          
          if (c.cost !== null && c.cost !== undefined) cost[c.cost] = (cost[c.cost] || 0) + 1;
          if (c.type) type[c.type] = (type[c.type] || 0) + 1;
          if (c.tribe) {
            trb[c.tribe] = (trb[c.tribe] || 0) + 1;
          }
          if (c.type === "MINION") {
            if (c.attack !== null && c.attack !== undefined) atk[c.attack] = (atk[c.attack] || 0) + 1;
            if (c.health !== null && c.health !== undefined) hp[c.health] = (hp[c.health] || 0) + 1;
          }
        });
        return { keywords: kw, selectors: sel, cost: cost, type: type, attack: atk, health: hp, tribes: trb };
      }

      function render() {
        var f = currentFilter();
        var cards = applyFilter(f);
        var agg = aggregate(cards);

        el.scopeCount.textContent = cards.length + (cards.length === 1 ? " card" : " cards");
        el.scopeDesc.textContent = describeScope(f);

        renderTypeSplit(agg.type, cards.length);
        renderBars(el.keywordBars, agg.keywords, "No keywords in this filter.");
        renderBars(el.tribeBars, agg.tribes, "No tribes in this filter.");
        renderBars(el.selectorBars, agg.selectors, "No targeting selectors in this filter.");
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

      // ----- small helpers -----
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
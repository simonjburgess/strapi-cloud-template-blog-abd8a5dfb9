(() => {
  "use strict";

  const $ = (sel) => document.querySelector(sel);
  const charts = {};
  let data = null;
  let halfHourDay = 0;

  const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const colour = (key) => css(`--${key}`) || css("--accent");
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const p = (v, dp = 2) => (v == null ? "–" : `${Number(v).toFixed(dp)}p`);
  const gbp = (v) => (v == null ? "–" : `£${Number(v).toFixed(2)}`);
  const shortDate = (iso) => new Date(`${iso}T12:00:00`).toLocaleDateString("en-GB", { day: "numeric", month: "short" });
  const nameOf = (key) => data.tariffs.find((t) => t.key === key)?.name ?? key;

  const store = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* storage unavailable */ } },
  };

  async function getJson(url) {
    const res = await fetch(url);
    if (res.status === 401) {
      location.href = "/login.html";
      throw new Error("Signing in…");
    }
    const body = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(body.error || body.title || `${res.status} ${res.statusText}`);
    return body;
  }

  async function init() {
    const config = await getJson("/api/config");
    const regionSel = $("#region");
    regionSel.innerHTML = `<option value="">${config.hasAccount ? "From my account" : "Default"}</option>` +
      config.regions.map((r) => `<option value="${r.code}">${r.code} · ${esc(r.name)}</option>`).join("");
    regionSel.value = store.get("region") ?? "";
    $("#days").value = store.get("days") ?? "30";

    $("#controls").addEventListener("submit", (e) => { e.preventDefault(); load(); });
    regionSel.addEventListener("change", load);
    $("#days").addEventListener("change", load);
    $("#findRegion").addEventListener("click", findRegion);
    $("#postcode").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); findRegion(); } });
    document.querySelectorAll(".tabs button").forEach((b) => b.addEventListener("click", () => {
      halfHourDay = Number(b.dataset.day);
      document.querySelectorAll(".tabs button").forEach((x) => x.setAttribute("aria-selected", String(x === b)));
      renderHalfHours();
    }));
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => data && render());

    await load();
  }

  async function findRegion() {
    const postcode = $("#postcode").value.trim();
    if (!postcode) return;
    setStatus(`Looking up ${postcode}…`);
    try {
      const r = await getJson(`/api/region?postcode=${encodeURIComponent(postcode)}`);
      $("#region").value = r.region;
      await load();
    } catch (e) {
      setStatus(`Couldn't find a region for that postcode: ${e.message}`);
    }
  }

  function setStatus(text) { $("#status").textContent = text; }

  async function load() {
    const region = $("#region").value;
    const days = $("#days").value;
    store.set("region", region);
    store.set("days", days);
    setStatus("Fetching prices from Octopus…");
    try {
      const qs = new URLSearchParams({ days });
      if (region) qs.set("region", region);
      data = await getJson(`/api/dashboard?${qs}`);
      if (!region) $("#region").value = "";
      render();
      setStatus(`Region ${data.region} · ${data.regionName} · ${shortDate(data.from)} to ${shortDate(data.to)} · updated ${new Date(data.generatedAt).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" })}`);
    } catch (e) {
      setStatus(`Couldn't load the dashboard: ${e.message}`);
    }
  }

  function render() {
    renderWarnings();
    renderCards();
    renderElectricity();
    renderHalfHours();
    renderGas();
    renderStanding();
    renderCosts();
  }

  function renderWarnings() {
    const ul = $("#warnings");
    ul.hidden = data.warnings.length === 0;
    ul.innerHTML = data.warnings.map((w) => `<li>${esc(w)}</li>`).join("");
  }

  function delta(today, tomorrow) {
    if (today == null || tomorrow == null) return "";
    const d = tomorrow - today;
    if (Math.abs(d) < 0.005) return `<span class="delta">no change</span>`;
    return `<span class="delta ${d > 0 ? "up" : "down"}">${d > 0 ? "▲" : "▼"} ${Math.abs(d).toFixed(2)}p</span>`;
  }

  function renderCards() {
    const elecToday = data.tariffs.map((t) => t.electricityToday).filter((v) => v != null);
    const cheapest = elecToday.length ? Math.min(...elecToday) : null;

    $("#cards").innerHTML = data.tariffs.map((t) => {
      const isCheapest = cheapest != null && t.electricityToday === cheapest;
      const elecLabel = t.key === "agile" ? "today's average" : "today";
      const gas = t.gasTariffCode ? `
          <div class="figure"><div class="label">Gas today</div><div class="value">${p(t.gasToday)}<small>/kWh</small></div></div>
          <div class="figure"><div class="label">Gas tomorrow</div><div class="value">${p(t.gasTomorrow)}${delta(t.gasToday, t.gasTomorrow)}</div></div>` : "";
      const now = t.key === "agile" ? `
          <div class="figure"><div class="label">Right now</div><div class="value">${p(t.electricityNow)}</div></div>
          <div class="figure"><div class="label">${data.days}-day average</div><div class="value">${p(t.electricityPeriodAverage)}</div></div>` : "";
      return `
        <article class="card" style="--swatch: var(--${t.key})">
          <h3>${esc(t.name)} ${isCheapest ? '<span class="badge">Cheapest today</span>' : ""}</h3>
          <div class="code">${esc(t.electricityTariffCode ?? t.productCode)}</div>
          <div class="figures">
            <div class="figure"><div class="label">Electricity ${elecLabel}</div><div class="value">${p(t.electricityToday)}<small>/kWh</small></div></div>
            <div class="figure"><div class="label">Electricity tomorrow</div><div class="value">${t.electricityTomorrow == null ? '<small>not published yet</small>' : p(t.electricityTomorrow)}${delta(t.electricityToday, t.electricityTomorrow)}</div></div>
            ${now}${gas}
          </div>
        </article>`;
    }).join("");
  }

  function baseOptions(yLabel) {
    const grid = css("--border");
    const text = css("--muted");
    return {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: "index", intersect: false },
      plugins: {
        legend: { labels: { color: text, usePointStyle: true, filter: (item) => !item.text.startsWith("_") } },
        tooltip: {
          filter: (item) => !item.dataset.label.startsWith("_"),
          callbacks: { label: (ctx) => `${ctx.dataset.label}: ${ctx.parsed.y == null ? "–" : yLabel(ctx.parsed.y)}` },
        },
      },
      scales: {
        x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 12 }, grid: { color: grid } },
        y: { ticks: { color: text, callback: (v) => yLabel(v) }, grid: { color: grid } },
      },
    };
  }

  function draw(id, config) {
    charts[id]?.destroy();
    charts[id] = new Chart(document.getElementById(id), config);
  }

  function line(key, values, extra = {}) {
    const c = colour(key);
    return {
      label: nameOf(key), data: values, borderColor: c, backgroundColor: c,
      borderWidth: 2, pointRadius: 0, pointHoverRadius: 4, spanGaps: false, ...extra,
    };
  }

  function renderElectricity() {
    const s = data.electricity;
    const datasets = [];
    for (const key of Object.keys(s.average)) {
      const stepped = key !== "agile";
      datasets.push(line(key, s.average[key], { stepped: stepped ? "middle" : false, tension: stepped ? 0 : 0.25 }));
      if (key === "agile") {
        const band = colour(key) + "26";
        datasets.push({ label: "_agileMax", data: s.max[key], borderWidth: 0, pointRadius: 0, fill: "+1", backgroundColor: band });
        datasets.push({ label: "_agileMin", data: s.min[key], borderWidth: 0, pointRadius: 0, fill: false });
      }
    }
    draw("elecChart", { type: "line", data: { labels: s.dates.map(shortDate), datasets }, options: baseOptions((v) => `${Number(v).toFixed(1)}p`) });
  }

  function renderHalfHours() {
    const day = data.halfHourly[halfHourDay];
    const hasAgile = (day.series.agile ?? []).some((v) => v != null);
    $("#halfHourNote").textContent = halfHourDay === 1 && !hasAgile
      ? "Tomorrow's Agile prices are usually published around 4pm."
      : `${new Date(`${day.date}T12:00:00`).toLocaleDateString("en-GB", { weekday: "long", day: "numeric", month: "long" })} · p/kWh inc VAT`;

    const datasets = Object.keys(day.series).map((key) => key === "agile"
      ? { ...line(key, day.series[key]), type: "bar", borderWidth: 0, backgroundColor: colour(key) + "b3", order: 2 }
      : line(key, day.series[key], { type: "line", stepped: "middle", borderDash: [6, 4], order: 1 }));
    const options = baseOptions((v) => `${Number(v).toFixed(1)}p`);
    options.scales.x.ticks.maxTicksLimit = 12;
    draw("halfHourChart", { type: "bar", data: { labels: day.labels, datasets }, options });
  }

  function renderGas() {
    const s = data.gas;
    const keys = Object.keys(s.average).filter((k) => s.average[k].some((v) => v != null));
    $("#gasPanel").hidden = keys.length === 0;
    if (!keys.length) return;
    const datasets = keys.map((key) => line(key, s.average[key], { stepped: "middle" }));
    draw("gasChart", { type: "line", data: { labels: s.dates.map(shortDate), datasets }, options: baseOptions((v) => `${Number(v).toFixed(2)}p`) });
  }

  function renderStanding() {
    $("#standing").innerHTML = `
      <thead><tr><th>Tariff</th><th>Electricity</th><th>Gas</th><th>Electricity avg unit rate (${data.days}d)</th><th>Gas avg unit rate (${data.days}d)</th></tr></thead>
      <tbody>${data.tariffs.map((t) => `
        <tr><td><span class="dot" style="--swatch: var(--${t.key})"></span>${esc(t.name)}</td>
          <td>${p(t.electricityStandingCharge)}</td><td>${t.gasTariffCode ? p(t.gasStandingCharge) : "n/a"}</td>
          <td>${p(t.electricityPeriodAverage)}</td><td>${t.gasTariffCode ? p(t.gasPeriodAverage) : "n/a"}</td></tr>`).join("")}
      </tbody>`;
  }

  function costTable(title, c) {
    // The server ranks fully priced tariffs first; one with gaps can't honestly be called cheapest.
    const best = c.tariffs[0]?.unpricedIntervals ? null : c.tariffs[0]?.totalPounds;
    return `
      <div>
        <h3>${title}: ${c.kwh.toFixed(1)} kWh over ${c.days} days (${shortDate(c.from)} to ${shortDate(c.to)})</h3>
        <div class="table-wrap"><table>
          <thead><tr><th>Tariff</th><th>Unit cost</th><th>Standing</th><th>Total</th><th>Effective p/kWh</th><th>vs cheapest</th></tr></thead>
          <tbody>${c.tariffs.map((t, i) => `
            <tr class="${i === 0 && best != null ? "best" : ""}"><td><span class="dot" style="--swatch: var(--${t.key})"></span>${esc(nameOf(t.key))}</td>
              <td>${gbp(t.unitCostPounds)}</td><td>${gbp(t.standingCostPounds)}</td><td>${gbp(t.totalPounds)}</td>
              <td>${p(t.averageUnitRate)}</td><td>${t.unpricedIntervals ? `<small>incomplete: ${t.unpricedIntervals} half-hours unpriced</small>`
                : best == null ? "–" : i === 0 ? "cheapest" : `+${gbp(t.totalPounds - best)}`}</td></tr>`).join("")}
          </tbody></table></div>
      </div>`;
  }

  function renderCosts() {
    const body = $("#costsBody");
    charts.costChart?.destroy();
    if (!data.hasAccount || !data.costs) {
      body.innerHTML = `<p class="empty">Add your Octopus API key and account number to price your real smart-meter usage under each tariff.
        Find both at octopus.energy → Account → Personal details → API access, then run
        <code>dotnet user-secrets set "Octopus:ApiKey" "sk_live_…"</code> and
        <code>dotnet user-secrets set "Octopus:AccountNumber" "A-1234ABCD"</code>.</p>`;
      return;
    }
    const { electricity, gas, notes } = data.costs;
    body.innerHTML = `<div class="costs-grid">
        ${electricity ? costTable("Electricity", electricity) : ""}
        ${gas ? costTable("Gas", gas) : ""}
        ${electricity ? '<div class="chart"><canvas id="costChart"></canvas></div>' : ""}
        <ul class="notes">${notes.map((n) => `<li>${esc(n)}</li>`).join("")}</ul>
      </div>`;
    if (!electricity) return;
    const keys = electricity.tariffs.map((t) => t.key);
    const datasets = keys.map((key) => ({ label: nameOf(key), data: electricity.daily.map((d) => d.poundsByTariff[key]), backgroundColor: colour(key), borderWidth: 0 }));
    const options = baseOptions((v) => `£${Number(v).toFixed(2)}`);
    options.plugins.title = { display: true, text: "Daily electricity cost", color: css("--muted") };
    draw("costChart", { type: "bar", data: { labels: electricity.daily.map((d) => shortDate(d.date)), datasets }, options });
  }

  // Service workers only run over HTTPS or on localhost; elsewhere the page still works, just not offline.
  if ("serviceWorker" in navigator && window.isSecureContext) {
    navigator.serviceWorker.register("/sw.js").catch(() => { /* offline support is optional */ });
  }

  init().catch((e) => setStatus(`Couldn't start: ${e.message}`));
})();

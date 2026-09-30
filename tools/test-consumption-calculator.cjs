const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const html = fs.readFileSync(path.join(__dirname, "..", "docs", "price-consumption-calculator.html"), "utf8");
const scripts = [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)];
const context = { module: { exports: {} } };
vm.runInNewContext(html.match(/<script id="calculator-model">([\s\S]*?)<\/script>/)[1], context);
const model = context.module.exports;
const line = (r, name) => {
  const item = r.lines.find(l => l.name.startsWith(name));
  assert.ok(item, `Missing service line: ${name}`);
  return item;
};
const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-7, `${actual} != ${expected}`);

test("every embedded script parses without browser dependencies", () => {
  scripts.forEach((script, index) => new vm.Script(script[1], { filename: `embedded-script-${index}.js` }));
  assert.equal(scripts.length, 3);
});

test("baseline is valid, totals reconcile, and all results are finite", () => {
  const state = model.defaults();
  assert.equal(model.validate(state).length, 0);
  const all = model.combined(state);
  assert.equal(all.results.development.initial, 880);
  assert.equal(all.results.staging.initial, 2200);
  assert.equal(all.results.production.initial, 44000);
  close(all.allocated, Object.values(all.results).reduce((sum, r) => sum + r.allocated, 0));
  for (const r of Object.values(all.results)) {
    close(r.allocated, r.lines.reduce((sum, l) => sum + l.allocated, 0));
    close(r.allocated, r.fixed + r.variable);
    for (const value of Object.values(r).filter(v => typeof v === "number")) assert.ok(Number.isFinite(value));
  }
});

test("question, follow-up, routed share and retry formulas use their intended units", () => {
  const s = model.defaults(), e = s.environments.development;
  Object.assign(e, { users: 1, days: 1, sessions: 1, questions: 100, followups: 20, routedFollowups: 50, retries: 10 });
  const r = model.calculate(s, "development");
  close(r.initial, 100);
  close(r.turns, 132);
  close(r.routed, 121);
  close(r.statements + r.definitions + r.explorations, r.routed);
  close(r.fuzzy, 121 * .6 * 2 * .2);
  close(r.reranks, r.fuzzy * .5);
});

test("direct model token billing includes reasoning output and eligible cached input", () => {
  const s = model.defaults(), e = s.environments.development;
  Object.assign(e, { users: 1, days: 1, sessions: 1, questions: 1, followups: 0, retries: 0, inputTokens: 1000, outputTokens: 100, cachedPct: 50 });
  const r = model.calculate(s, "development");
  close(line(r, "Outer routing").allocated, (500 * s.prices.modelInput + 500 * s.prices.modelCached + 100 * s.prices.modelOutput) / 1e6);
  close(line(r, "Constrained").allocated, r.reranks * (e.rerankInput * s.prices.modelInput + e.rerankOutput * s.prices.modelOutput) / 1e6);
});

test("zero demand retains provisioned service costs and has no per-question division", () => {
  const s = model.defaults();
  s.environments.development.users = 0;
  const r = model.calculate(s, "development");
  assert.equal(r.routed, 0);
  assert.equal(r.perQuestion, null);
  assert.equal(line(r, "Outer routing").allocated, 0);
  assert.ok(line(r, "Azure AI Search").allocated > 0);
  assert.ok(line(r, "Fabric F").allocated > 0);
  assert.ok(r.allocated > 0);
});

test("exact metadata avoids search and reranking without removing index service cost", () => {
  const s = model.defaults();
  s.environments.development.fuzzyPct = 0;
  const r = model.calculate(s, "development");
  assert.equal(r.fuzzy, 0);
  assert.equal(r.queryEmbeddingTokens, 0);
  assert.equal(line(r, "Semantic").allocated, 0);
  assert.equal(line(r, "Constrained").allocated, 0);
  assert.ok(line(r, "Azure AI Search").allocated > 0);
  assert.ok(r.publicationTokens > 0);
});

test("Search replicas times partitions times full month, not active working hours", () => {
  const s = model.defaults(), e = s.environments.staging;
  e.searchReplicas = 2; e.searchPartitions = 3; e.hostHours = 220;
  close(line(model.calculate(s, "staging"), "Azure AI Search").allocated, 2 * 3 * 730 * .336);
});

test("allowances never make semantic or logging charges negative", () => {
  const s = model.defaults(), e = s.environments.development;
  e.semanticFree = 1000000; e.logsFree = 100000; e.retentionGB = 0;
  const r = model.calculate(s, "development");
  assert.equal(line(r, "Semantic").allocated, 0);
  assert.equal(line(r, "Application Insights").allocated, 0);
});

test("two implementations duplicate hosting and state, not demand, Search or Fabric", () => {
  const s = model.defaults();
  const one = model.calculate(s, "development");
  s.environments.development.implementations = 2;
  const two = model.calculate(s, "development");
  close(line(two, "Foundry native").allocated, line(one, "Foundry native").allocated * 2);
  close(line(two, "Managed conversation").allocated, line(one, "Managed conversation").allocated * 2);
  close(two.initial, one.initial);
  for (const name of ["Azure AI Search", "Fabric F", "Outer routing"])
    close(line(two, name).allocated, line(one, name).allocated);
  const c1 = model.calculate(s, "production");
  s.environments.production.implementations = 2;
  const c2 = model.calculate(s, "production");
  close(line(c2, "Cosmos NoSQL").allocated, line(c1, "Cosmos NoSQL").allocated * 2);
  close(line(c2, "Cosmos transactional").allocated, line(c1, "Cosmos transactional").allocated * 2);
});

test("shared allocation and existing spend are separate from the full capacity invoice", () => {
  const s = model.defaults(), e = s.environments.development;
  e.fabricAllocation = 25; e.fabricNew = 0; e.searchAllocation = 50; e.searchNew = 20;
  const r = model.calculate(s, "development");
  close(line(r, "Fabric F").allocated, 2 * 220 * s.prices.fabricCU * .25);
  assert.equal(line(r, "Fabric F").incremental, 0);
  close(line(r, "Azure AI Search").incremental, 730 * .101 * .5 * .2);
  assert.ok(line(r, "OneLake").incremental > 0);
  assert.ok(r.incremental < r.allocated);
});

test("Cosmos autoscale enforces 10 percent floor and multiplies regions once", () => {
  const s = model.defaults(), e = s.environments.production;
  e.cosmosPeak = 0;
  const r = model.calculate(s, "production");
  assert.equal(r.billableRU, 400);
  close(line(r, "Cosmos NoSQL").allocated, 400 / 100 * .012 * 730 * 2);
  assert.ok(!r.lines.some(l => l.name.startsWith("Managed conversation")));
});

test("discount is confined to eligible Azure meters; contingency applies only to budget", () => {
  const s = model.defaults(), base = model.calculate(s, "development");
  s.global.discountPct = 10; s.global.contingencyPct = 20;
  const r = model.calculate(s, "development");
  close(line(r, "Azure AI Search").allocated, line(base, "Azure AI Search").allocated * .9);
  close(line(r, "Foundry native").allocated, line(base, "Foundry native").allocated * .9);
  close(line(r, "Managed conversation").allocated, line(base, "Managed conversation").allocated);
  close(r.annual, r.allocated * 12 * 1.2);
});

test("demand sensitivity changes usage, not capacity allocations or publish frequency", () => {
  const s = model.defaults(), low = model.calculate(s, "production", .5), high = model.calculate(s, "production", 2);
  close(high.initial, low.initial * 4);
  assert.ok(high.allocated > low.allocated);
  close(high.fixed, low.fixed);
  close(high.publicationTokens, low.publicationTokens);
});

test("Fabric CU estimates are diagnostics, not an additional per-query invoice", () => {
  const s = model.defaults(), base = model.calculate(s, "production");
  s.environments.production.exploreCU = 1000000;
  const loaded = model.calculate(s, "production");
  close(loaded.allocated, base.allocated);
  assert.ok(loaded.fabricLoad > 100);
  assert.ok(loaded.warnings.some(w => w.includes("Re-size")));
});

test("paused Fabric and stopped hosting with demand are explicitly flagged", () => {
  const s = model.defaults(), e = s.environments.development;
  e.fabricHours = 0; e.hostHours = 0;
  const r = model.calculate(s, "development");
  assert.equal(r.fabricLoad, null);
  assert.ok(r.warnings.some(w => w.includes("cannot run")));
  assert.ok(r.warnings.some(w => w.includes("zero provisioned hours")));
});

test("publication and first seed are separate, and vector footprint is not another service", () => {
  const s = model.defaults(), r = model.calculate(s, "development");
  close(r.initialEmbeddingCost, 114 * 160 / 1e6 * s.prices.embedding);
  close(r.publicationTokens, 114 * 160 * 2);
  close(r.vectorMB, 114 * 3 * 1536 * 4 / 1e6);
});

test("environment inclusion affects combined budget without erasing scenario comparisons", () => {
  const s = model.defaults();
  s.environments.development.included = 0; s.environments.staging.included = 0;
  const all = model.combined(s);
  close(all.allocated, all.results.production.allocated);
  s.environments.production.included = 0;
  assert.equal(model.combined(s).allocated, 0);
});

test("invalid numeric values, shape, routing sums and unsupported capacity fail explicitly", () => {
  const cases = [
    s => { s.environments.development.users = -1; },
    s => { s.environments.development.inputTokens = null; },
    s => { s.prices.hostCPU = NaN; },
    s => { s.prices.modelInput = Infinity; },
    s => { s.prices.extra = 2; },
    s => { delete s.global.monthHours; },
    s => { s.version = 999; },
    s => { s.environments.development.kpiPct = 99; },
    s => { s.environments.development.searchPartitions = 4; },
    s => { s.environments.production.fabricCU = 3; },
    s => { s.environments.production.cosmosMax = 1500; },
    s => { s.environments.production.cosmosGB = 10000; },
    s => { s.environments.development.hostHours = 744; },
    s => { s.environments.development.retainedIndexes = 2.5; },
    s => { s.environments.development.searchTier = "<img onerror=alert(1)>"; }
  ];
  for (const change of cases) {
    const s = model.defaults(); change(s);
    assert.ok(model.validate(s).length > 0);
    assert.throws(() => model.calculate(s, "development"));
  }
  assert.ok(model.validate(null).length > 0);
  assert.ok(model.validate([]).length > 0);
});

test("JSON roundtrip retains the exact scenario and edited rate evidence", () => {
  const s = model.defaults(); s.prices.searchBasic = .111;
  const imported = JSON.parse(JSON.stringify(s));
  assert.equal(model.validate(imported).length, 0);
  close(model.calculate(imported, "development").allocated, model.calculate(s, "development").allocated);
  assert.equal(line(model.calculate(imported, "development"), "Azure AI Search").evidence, "User override");
});

test("artifact is self-contained with no CDN, telemetry, cloud credentials or runtime fetch", () => {
  assert.ok(!/<script[^>]+src=|<link[^>]+href=|<iframe/i.test(html));
  assert.ok(!/\bfetch\(|XMLHttpRequest|sendBeacon|WebSocket/.test(html));
  assert.ok(html.includes("Staging/Production component catalogue, tiers and sizing are assumptions"));
});

test("native compute bills CPU and memory hours for the selected lifecycle", () => {
  const s = model.defaults(), e = s.environments.development;
  const provisioned = model.calculate(s, "development");
  close(line(provisioned, "Foundry native").allocated, 730 * (.10934 + 2 * .01298));
  e.hostMode = "Per-session sandboxes";
  const sessions = model.calculate(s, "development");
  close(sessions.hostInstanceHours, 10 * 22 * 1 * (10 + 15) / 60);
  close(line(sessions, "Foundry native").allocated, sessions.hostInstanceHours * .1353);
  assert.equal(line(sessions, "Foundry native").capacity, false);
  e.implementations = 2; e.replicas = 10;
  close(model.calculate(s, "development").hostInstanceHours, sessions.hostInstanceHours);
  close(model.calculate(s, "development", 2).hostInstanceHours, sessions.hostInstanceHours * 2);
});

test("Fabric inference CU conversion and explicit overage do not double-count capacity", () => {
  const s = model.defaults(), e = s.environments.development;
  const base = model.calculate(s, "development");
  assert.equal(base.fabricInferencePerCall, 400);
  e.fabricCached = 50;
  const cached = model.calculate(s, "development");
  assert.equal(cached.fabricInferencePerCall, 310);
  assert.ok(cached.fabricSeconds < base.fabricSeconds);
  close(cached.allocated, base.allocated);
  e.overageHours = 10;
  close(model.calculate(s, "development").allocated - cached.allocated, 5.7);
});

test("malformed JSON sections consistently return validation errors instead of throwing", () => {
  for (const field of ["global", "environments", "prices"]) {
    for (const invalid of [null, [], 42, "", {}, { unexpected: 1 }]) {
      const s = model.defaults(); s[field] = invalid;
      assert.ok(model.validate(s).length > 0);
      assert.throws(() => model.calculate(s, "development"), /invalid or incompatible/);
    }
  }
  const s = model.defaults();
  s.environments.development = null;
  assert.ok(model.validate(s).length > 0);
  for (const factor of [null, NaN, Infinity, -1, 101]) assert.throws(() => model.calculate(model.defaults(), "development", factor));
  assert.throws(() => model.calculate(model.defaults(), "unknown"));
});

test("customer-facing qualification is explicit and Copilot Studio pricing is absent", () => {
  const s = model.defaults();
  assert.ok(html.includes("Actual prices may vary depending on your Microsoft agreement"));
  assert.ok(html.includes("Plan your deployment budget"));
  assert.ok(!html.includes("with your customer"));
  assert.ok(!Object.keys(s.prices).some(key => /copilot|credit/i.test(key)));
  assert.ok(!Object.keys(s.environments.development).some(key => /credit|entitled/i.test(key)));
  const r = model.calculate(s, "development");
  assert.ok(!r.lines.some(l => /copilot|kpipedia/i.test(l.name)));
  assert.ok(r.definitions > 0);
  assert.ok(r.warnings.some(w => w.includes("Copilot Studio / KPIpedia pricing is excluded")));
});

test("cross-field validation identifies every editable field involved", () => {
  const s = model.defaults();
  s.environments.development.kpiPct = 0;
  let error = model.validate(s).find(e => e.path.endsWith(".statementPct"));
  assert.ok(error.relatedPaths.includes("environments.development.kpiPct"));
  assert.ok(error.relatedPaths.includes("environments.development.explorePct"));
  s.environments.development.kpiPct = 20;
  s.global.monthHours = 200;
  error = model.validate(s).find(e => e.path === "environments.development.fabricHours");
  assert.ok(error.relatedPaths.includes("global.monthHours"));
  assert.ok(model.validate(s).some(e => e.path === "environments.development.hostHours"));
});

test("controller cost categories reconcile to the exact IT totals without changing the scenario", () => {
  const s = model.defaults();
  s.prices.modelInput = .8;
  s.global.discountPct = 12; s.global.demandFactor = 1.5;
  s.environments.production.fabricAllocation = 40;
  s.environments.production.extraMonthly = 123;
  const before = JSON.stringify(s);
  for (const key of Object.keys(model.environments)) {
    const business = model.controllerSummary(s, key), technical = model.calculate(s, key);
    close(business.costs.reduce((sum, cost) => sum + cost.amount, 0), technical.allocated);
    close(business.beforeQuestions + business.addedUsage, technical.allocated);
    close(business.perPerson, technical.allocated / s.environments[key].users);
    assert.ok(!business.costs.some(cost => /Copilot|KPIpedia|token|RU\/s/.test(cost.label)));
  }
  assert.equal(JSON.stringify(s), before);
});

test("controller starting cost includes storage and maintenance, not only capacity lines", () => {
  const s = model.defaults();
  for (const mode of ["Provisioned baseline", "Per-session sandboxes"]) {
    s.environments.production.hostMode = mode;
    const summary = model.controllerSummary(s, "production");
    const zeroDemand = model.calculate(s, "production", 0);
    close(summary.beforeQuestions, zeroDemand.allocated);
    assert.ok(summary.beforeQuestions > zeroDemand.fixed);
    assert.ok(summary.addedUsage > 0);
    s.global.demandFactor = 0;
    assert.equal(model.controllerSummary(s, "production").addedUsage, 0);
    s.global.demandFactor = 1;
  }
});

test("controller per-person average is scoped to one area and handles zero users", () => {
  const s = model.defaults();
  const daily = model.controllerSummary(s, "production").perPerson;
  s.environments.development.users = 500;
  s.environments.production.included = 0;
  close(model.controllerSummary(s, "production").perPerson, daily);
  s.environments.production.users = 0;
  const empty = model.controllerSummary(s, "production");
  assert.equal(empty.perPerson, null);
  assert.equal(empty.addedUsage, 0);
  assert.ok(empty.beforeQuestions > 0);
  assert.throws(() => model.controllerSummary(s, "missing"));
  s.environments.production.users = -1;
  assert.throws(() => model.controllerSummary(s, "production"));
});

test("every technical warning has a plain-language counterpart including infeasible budgets", () => {
  const s = model.defaults(), e = s.environments.production;
  Object.assign(e, { hostHours: 0, fabricHours: 0, retainedIndexes: 99, searchAllocation: 50, endpoints: 0, privateGB: 10 });
  s.prices.embedding = 0;
  const r = model.calculate(s, "production");
  assert.equal(r.businessWarnings.length, r.warnings.length);
  for (const warning of r.businessWarnings) assert.ok(typeof warning === "string" && warning.length > 0);
  assert.ok(r.businessWarnings.some(w => w.includes("planned workload cannot run")));
  assert.ok(r.businessWarnings.some(w => w.includes("no running time")));
  assert.ok(r.businessWarnings.some(w => w.includes("price has been set to zero")));
  e.fabricHours = 1;
  assert.ok(model.calculate(s, "production").businessWarnings.some(w => w.includes("exceeds the planned spare capacity")));
});

test("controller view is the offline default and retains pricing boundaries", () => {
  assert.ok(html.includes('<body data-view="controller">'));
  assert.ok(html.includes('id="it-view"'));
  assert.ok(html.includes("not a per-person licence fee"));
  assert.ok(html.includes("one-off implementation and migration"));
  assert.ok(html.includes("higher demand may require IT to increase the service size"));
  assert.equal(Object.keys(model.businessAreas).join(","), Object.keys(model.environments).join(","));
});

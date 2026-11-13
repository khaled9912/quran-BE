#!/usr/bin/env node
// Analysis for the IIS access logs MonsterASP hands out as CSV exports
// (DateTime,Status,SubStatus,ClientIp,Method,UriStem,UriQuery,TimeTakenMs,
// BytesSent,Referer,UserAgent). Those files are the only server-side traffic
// signal available on shared IIS hosting, and they also contain valid SignalR
// tickets in query strings - hence the built-in redacted copy.
//
//   node tools/access-log-report.mjs ~/Downloads/site103604_accesslog_20261112.csv
//   node tools/access-log-report.mjs --json a.csv b.csv      # machine-readable
//   node tools/access-log-report.mjs --redact out/ a.csv     # shareable copies
//
// Exit code is 1 when any route breaches the error budget, so this can run as
// a scheduled check without anyone reading the output.

import fs from 'node:fs';
import path from 'node:path';

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
const SENSITIVE_PARAMS = ['access_token', 'token', 'code', 'otp', 'password', 'apikey'];

/**
 * Errors that carry no information because they are the designed answer. They
 * stay in the tables and out of the alert, so a budget breach still means
 * something on the day nobody uploaded an avatar.
 */
const EXPECTED = [
  { route: '/api/auth/me', status: '401', reason: 'anonymous session probe' },
  { route: '/api/users/{id}/avatar', status: '404', reason: 'user has no photo' },
  { method: 'DELETE', status: '409', reason: 'business rule refused a state change' },
];

const args = process.argv.slice(2);
const opts = { budget: 5, min: 20, slowMs: 5000, json: false, redactDir: null, files: [] };
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--json') opts.json = true;
  else if (a === '--redact') opts.redactDir = args[++i];
  else if (a === '--budget') opts.budget = Number(args[++i]);
  else if (a === '--min') opts.min = Number(args[++i]);
  else if (a === '--slow-ms') opts.slowMs = Number(args[++i]);
  else if (a === '--help' || a === '-h') { console.log(fs.readFileSync(new URL(import.meta.url), 'utf8').split('\n').slice(1, 12).map(l => l.replace(/^\/\/ ?/, '')).join('\n')); process.exit(0); }
  else opts.files.push(a);
}
if (!opts.files.length) { console.error('usage: access-log-report.mjs [options] <accesslog.csv ...>'); process.exit(2); }

function parseCsv(text) {
  const rows = [];
  let field = '', row = [], quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else quoted = false; }
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(field); field = ''; }
    else if (c === '\n') { row.push(field); rows.push(row); field = ''; row = []; }
    else if (c !== '\r') field += c;
  }
  if (field !== '' || row.length) { row.push(field); rows.push(row); }
  return rows;
}

function load(file) {
  const raw = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
  const all = parseCsv(raw);
  const header = (all[0] || []).map(h => h.trim());
  if (!header.join(',').startsWith('DateTime,Status,SubStatus,ClientIp,Method,UriStem'))
    throw new Error(`${file}: not a MonsterASP access-log export (header: ${header.join(',')})`);
  return all.slice(1)
    .filter(r => r.length === header.length && r[0].trim() !== '')
    .map(r => {
      const o = Object.fromEntries(header.map((h, i) => [h, r[i]]));
      return {
        ts: o.DateTime,
        status: String(o.Status ?? '').trim(),
        sub: o.SubStatus,
        ip: o.ClientIp,
        method: o.Method,
        stem: o.UriStem,
        query: o.UriQuery ?? '',
        ms: Number(o.TimeTakenMs) || 0,
        bytes: Number(o.BytesSent) || 0,
        referer: o.Referer ?? '',
        ua: o.UserAgent ?? '',
      };
    });
}

const route = r => r.stem.replace(GUID, '{id}');
const client = r =>
  /facebookexternalhit|LinkedInBot|bot|crawl|spider|Slackbot|Twitterbot/i.test(r.ua) ? 'bot'
    : /^okhttp|; w\)|ReactNative|Expo|CFNetwork/i.test(r.ua) || !/Mozilla/.test(r.ua) ? 'app'
      : /Mobile|Android|iPhone/i.test(r.ua) ? 'mobile-browser'
        : 'desktop-browser';
const isExpected = r => EXPECTED.find(e =>
  (e.route ? e.route === route(r) : true) &&
  (e.method ? e.method === r.method : true) &&
  (e.status ? e.status === r.status : true));

const rows = opts.files.flatMap(f => load(f));
if (!rows.length) { console.error('no data rows found'); process.exit(2); }
const times = rows.map(r => r.ts).sort();

const group = key => {
  const map = new Map();
  for (const r of rows) {
    const k = key(r);
    if (!map.has(k)) map.set(k, []);
    map.get(k).push(r);
  }
  return map;
};
const pct = (n, d) => (d ? (100 * n) / d : 0);
const fmtPct = n => n.toFixed(1).padStart(5);
const pad = (s, n) => String(s).padEnd(n);
const perc = (values, p) => {
  const s = [...values].sort((a, b) => a - b);
  return s.length ? s[Math.min(s.length - 1, Math.floor(p * s.length))] : 0;
};

// 101 is a successful WebSocket upgrade whose TimeTakenMs is the socket's whole
// lifetime, so it must not join either the error or the latency numbers.
const successful = r => r.status.startsWith('2') || r.status === '101' || r.status === '304';

const perRoute = [...group(route).entries()].map(([stem, list]) => {
  const errors = list.filter(r => !successful(r));
  const expected = errors.filter(isExpected);
  return {
    route: stem,
    requests: list.length,
    errors: errors.length,
    unexpected: errors.length - expected.length,
    rate: pct(errors.length, list.length),
    unexpectedRate: pct(errors.length - expected.length, list.length),
    statuses: [...group2(list, r => r.status)].sort((a, b) => b[1] - a[1]).map(([s, n]) => `${s}:${n}`).join(' '),
    expectedReason: [...new Set(expected.map(r => isExpected(r).reason))].join('; '),
    p50: perc(list.map(r => r.ms), 0.5),
    p95: perc(list.map(r => r.ms), 0.95),
    max: Math.max(0, ...list.map(r => r.ms)),
    ips: new Set(list.map(r => r.ip)).size,
    anon: list.filter(r => r.status === '401').length,
  };
}).sort((a, b) => b.requests - a.requests);

function group2(list, key) {
  const m = new Map();
  for (const r of list) { const k = key(r); m.set(k, (m.get(k) || 0) + 1); }
  return [...m.entries()];
}

const breaches = perRoute.filter(r =>
  r.requests >= opts.min && r.unexpected > 0 && r.unexpectedRate > opts.budget);

const slow = rows.filter(r => r.ms > opts.slowMs && r.status !== '101');
const clusters = [...group(r => r.ts.slice(0, 19)).entries()]
  .filter(([, list]) => list.filter(r => r.ms > opts.slowMs && r.status !== '101').length >= 3)
  .map(([second, list]) => {
    const hit = list.filter(r => r.ms > opts.slowMs && r.status !== '101');
    return {
      second,
      requests: hit.length,
      routes: new Set(hit.map(route)).size,
      slowest: Math.max(...hit.map(r => r.ms)),
      sample: [...new Set(hit.map(route))].slice(0, 6).join(', '),
    };
  })
  .sort((a, b) => b.requests - a.requests);

const pollers = [...group(r => `${r.ip}|${route(r)}`).entries()]
  .map(([k, list]) => ({ key: k, requests: list.length, route: route(list[0]), ip: list[0].ip,
    statuses: [...group2(list, r => r.status)].sort((a, b) => b[1] - a[1]).map(([s, n]) => `${s}:${n}`).join(' '),
    first: list.map(r => r.ts).sort()[0], last: list.map(r => r.ts).sort().pop() }))
  .filter(p => p.requests >= opts.min)
  .sort((a, b) => b.requests - a.requests);

const tickets = rows.filter(r => /(^|&)access_token=/.test(r.query));
const iisPages = rows.filter(r => r.status === '404' && r.bytes >= 1000);
const upgraded = rows.filter(r => r.status === '101');

const summary = {
  files: opts.files,
  rows: rows.length,
  windowStart: times[0],
  windowEnd: times[times.length - 1],
  ips: new Set(rows.map(r => r.ip)).size,
  userAgents: new Set(rows.map(r => r.ua)).size,
  statuses: Object.fromEntries(group2(rows, r => r.status).sort((a, b) => b[1] - a[1])),
  clients: Object.fromEntries(group2(rows, client).sort((a, b) => b[1] - a[1])),
  rateLimited: rows.filter(r => r.status === '429').length,
  serverErrors: rows.filter(r => r.status.startsWith('5')).length,
  loggedTickets: tickets.length,
  iisErrorPages: iisPages.length,
  webSockets: upgraded.length,
  socketLifetimeMs: { p50: perc(upgraded.map(r => r.ms), 0.5), max: Math.max(0, ...upgraded.map(r => r.ms)) },
  latencyMs: { p50: perc(rows.filter(r => r.status !== '101').map(r => r.ms), 0.5), p95: perc(rows.filter(r => r.status !== '101').map(r => r.ms), 0.95) },
};

if (opts.redactDir) {
  fs.mkdirSync(opts.redactDir, { recursive: true });
  for (const file of opts.files) {
    const raw = fs.readFileSync(file, 'utf8');
    // Parameters are matched after a comma (start of the query field) or an
    // & (later in the same field), so a param name appearing in a path,
    // referer or user agent is left alone.
    const param = new RegExp(`((?:^|[,&])(?:${SENSITIVE_PARAMS.join('|')})=)[^,&\\r\\n]*`, 'gi');
    const redacted = raw.split('\n')
      .map(line => line.startsWith('DateTime,') ? line : line.replace(param, '$1[redacted]'))
      .join('\n');
    const out = path.join(opts.redactDir, path.basename(file).replace(/\.csv$/, '.redacted.csv'));
    fs.writeFileSync(out, redacted);
    console.error(`redacted copy: ${out}`);
  }
}

if (opts.json) {
  console.log(JSON.stringify({ summary, budgetPct: opts.budget, breaches, routes: perRoute, coldStartClusters: clusters, pollers }, null, 2));
  process.exit(breaches.length ? 1 : 0);
}

const line = (n = 100) => '─'.repeat(n);
console.log(`Ana Quran API · access-log report`);
console.log(`${summary.rows} rows from ${summary.files.length} file(s) · ${summary.windowStart} → ${summary.windowEnd}`);
console.log(`${summary.ips} client IPs · ${summary.userAgents} user agents · p50 ${summary.latencyMs.p50} ms · p95 ${summary.latencyMs.p95} ms`);
console.log(`status ${Object.entries(summary.statuses).map(([s, n]) => `${s}:${n}`).join('  ')}`);
console.log(`clients ${Object.entries(summary.clients).map(([s, n]) => `${s}:${n}`).join('  ')}`);
console.log(`429 ${summary.rateLimited} · 5xx ${summary.serverErrors} · WebSocket upgrades ${summary.webSockets} (p50 ${summary.socketLifetimeMs.p50} ms, max ${summary.socketLifetimeMs.max} ms) · logged tickets ${summary.loggedTickets} · IIS error pages ${summary.iisErrorPages}`);

console.log(`\n${line()}\nERROR BUDGET  (alert: >${opts.budget}% unexpected non-2xx on ${opts.min}+ requests)\n${line()}`);
if (!breaches.length) console.log('  no route breaches the budget.');
else for (const r of breaches) console.log(`  ALERT  ${pad(r.route, 46)} ${pad(r.requests, 6)} ${fmtPct(r.unexpectedRate)}%  ${r.statuses}`);
console.log(`\n  ${pad('route', 46)} ${pad('reqs', 6)} ${pad('err%', 7)} ${pad('p50', 7)} ${pad('p95', 7)} statuses`);
for (const r of perRoute.slice(0, 20)) {
  const tag = r.expectedReason ? `  (${r.expectedReason})` : '';
  console.log(`  ${pad(r.route, 46)} ${pad(r.requests, 6)} ${fmtPct(r.rate)}% ${pad(r.p50, 7)} ${pad(r.p95, 7)} ${r.statuses}${tag}`);
}
if (perRoute.length > 20) console.log(`  … ${perRoute.length - 20} more route(s)`);

console.log(`\n${line()}\nCOLD START  (${slow.length} requests over ${opts.slowMs} ms)\n${line()}`);
if (!clusters.length) console.log('  no cluster of 3+ slow requests inside one second.');
else for (const c of clusters.slice(0, 8))
  console.log(`  ${c.second}  ${pad(c.requests, 3)} requests across ${pad(c.routes, 3)} routes, slowest ${c.slowest} ms`);
if (clusters.length) console.log('  one second holding many unrelated slow routes is a wake-up, not a slow query.');

console.log(`\n${line()}\nPOLLING  (same client IP + route, ${opts.min}+ hits)\n${line()}`);
if (!pollers.length) console.log('  none.');
else for (const p of pollers.slice(0, 10))
  console.log(`  ${pad(p.requests, 5)}  ${pad(p.route, 44)} ${pad(p.ip, 16)} ${p.statuses}  ${p.first.slice(11)} → ${p.last.slice(11)}`);

console.log(`\n${line()}\nNOTES\n${line()}`);
console.log(`  · The API is behind the frontend's same-origin rewrite, so these IPs are hosting egress`);
console.log(`    addresses, not users. Rate-limit buckets and audit rows only see real clients once`);
console.log(`    Forwarding__TrustedProxies is configured.`);
if (summary.loggedTickets) console.log(`  · ${summary.loggedTickets} row(s) carry a SignalR ticket in the query string. Share this export only as its --redact copy.`);
if (iisPages.length) console.log(`  · ${iisPages.length} 404(s) returned a ${iisPages[0].bytes}-byte body: IIS's own page, so the route never reached the app.`);
console.log(`  · With the fail-closed fallback policy an unmatched route answers 401 to anonymous callers`);
console.log(`    and 404 to signed-in ones - one endpoint producing both is a missing route, not auth noise.`);

process.exit(breaches.length ? 1 : 0);

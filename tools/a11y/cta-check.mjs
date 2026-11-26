// Calls-to-action check (wiki page 07): opens every report page of a finished systematic and multivocal run, as the
// run's owner and as a reader, and checks that every link and button works, on the web and in the printed PDF.
// Same-origin links must answer 2xx; external links must be absolute and well formed (they are not fetched); no link may
// be javascript:; buttons must do what they say (a citation opens its card and Close closes it, Copy link copies, Print
// opens the print window, the More menu opens) with no console error; and the PDF's link annotations must be absolute
// http(s) addresses, with the front page's links to the report, the archive and the protocol, each answering 2xx.
// The runs are seeded by CtaSeedTests (CTA_SEED_DIR, CTA_SEED_KEYS). Run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 CTA_KEYS=/path/keys.json node cta-check.mjs
import { chromium } from 'playwright';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const runs = JSON.parse(readFileSync(process.env.CTA_KEYS ?? 'cta-keys.json', 'utf8'));
const out = process.env.CTA_OUT || null;
if (out) mkdirSync(out, { recursive: true });
const pagesOf = {
  systematic: id => [`/spec-matrix/${id}`, `/review/${id}`],
  multivocal: id => [`/mlr-paper/${id}`, `/mlr-report/${id}`, `/mlr/${id}`],
};
const frontPages = /^\/(spec-matrix|mlr-paper)\//;
// Buttons that change or cost something are left alone: they are checked by the unit tests.
const leaveAlone = /delete|check again|save|undo|cancel|start|submit|stop|send|resume|edit/i;
const wait = { timeout: 10000 };
const problems = [];
const fetched = new Map();

async function status(request, url) {
  if (!fetched.has(url)) fetched.set(url, request.get(url, { maxRedirects: 5, timeout: 30000 }).then(r => r.status(), e => `error ${e.message}`));
  return fetched.get(url);
}

function external(href) {
  try {
    const u = new URL(href);
    return ['http:', 'https:', 'mailto:'].includes(u.protocol) && (u.protocol === 'mailto:' || u.hostname.includes('.'));
  } catch { return false; }
}

async function checkLinks(page, label) {
  const links = await page.$$eval('a[href]', as => as.map(a => ({ raw: a.getAttribute('href'), href: a.href, text: a.textContent.trim().slice(0, 40) })));
  let count = 0;
  for (const { raw, href, text } of links) {
    count++;
    if (/^\s*javascript:/i.test(raw)) { problems.push(`${label}: link "${text}" is javascript:`); continue; }
    if (raw.startsWith('#')) {
      const id = decodeURIComponent(raw.slice(1));
      if (id && !(await page.$(`[id="${id.replace(/"/g, '\\"')}"]`))) problems.push(`${label}: link "${text}" points to #${id}, which is not on the page`);
      continue;
    }
    if (href.startsWith(base + '/')) {
      const s = await status(page.request, href.split('#')[0]);
      if (!(typeof s === 'number' && s >= 200 && s < 300)) problems.push(`${label}: link "${text}" (${raw}) answered ${s}`);
    } else if (!external(href)) problems.push(`${label}: link "${text}" (${raw}) is not an absolute web address`);
  }
  // A web address in a reference must be a link, so a reader of the page or the PDF can follow it.
  const bare = await page.$$eval('.journal-refs li', items => items.flatMap(li => {
    const linked = [...li.querySelectorAll('a')].map(a => a.textContent);
    return (li.textContent.match(/https?:\/\/[^\s)]+/g) ?? []).map(u => u.replace(/[.,;:]+$/, '')).filter(u => !linked.some(l => l.includes(u)));
  }));
  bare.slice(0, 5).forEach(u => problems.push(`${label}: the reference address ${u} is not a link`));
  return count;
}

async function clickButtons(page, label, owner) {
  let clicked = 0;
  for (const summary of await page.locator('details > summary:visible').all()) { await summary.click(wait); clicked++; }
  const cite = page.locator('button[id^="cite-"]').first();
  if (await cite.count()) {
    await cite.scrollIntoViewIfNeeded();
    await cite.click(wait);
    const close = page.getByRole('button', { name: 'Close', exact: true }).first();
    await close.waitFor({ state: 'visible', ...wait });
    await close.click(wait);
    await close.waitFor({ state: 'hidden', ...wait });
    clicked++;
  }
  const print = page.getByRole('button', { name: /Print or save as PDF/ });
  if (await print.count()) {
    await print.first().click(wait);
    await page.waitForFunction(() => (window.__printed ?? 0) > 0, null, wait);
    clicked++;
  }
  const copy = page.getByRole('button', { name: /Copy link/ });
  if (await copy.count()) {
    await page.locator('details > summary').first().click(wait).catch(() => {});
    await copy.first().click(wait);
    await page.getByRole('button', { name: /^(Link copied|Copied)$/ }).first().waitFor(wait);
    const copied = await page.evaluate(() => navigator.clipboard.readText());
    if (!copied.startsWith(base)) problems.push(`${label}: Copy link copied "${copied}"`);
    clicked++;
  }
  for (const name of [/Copy for the methods/, /Copy a short declaration/]) {
    const button = page.getByRole('button', { name });
    if (await button.count()) {
      await button.first().click(wait);
      if ((await page.evaluate(() => navigator.clipboard.readText())).length < 20) problems.push(`${label}: "${name.source}" copied nothing`);
      clicked++;
    }
  }
  for (const tab of await page.getByRole('tab').all()) { await tab.click(wait); clicked++; }
  // Every other visible button that changes nothing on the server must at least not raise an error.
  for (const button of await page.locator('button:visible').all()) {
    const name = ((await button.getAttribute('aria-label')) ?? (await button.textContent()) ?? '').trim();
    if (!name || leaveAlone.test(name) || /^cite-/.test((await button.getAttribute('id')) ?? '') || /Close|Copy|Print|theme/i.test(name)) continue;
    if (!owner && /note/i.test(name)) continue;
    if (await button.isDisabled()) continue;
    await button.click({ timeout: 3000 }).catch(() => {});
    clicked++;
  }
  return clicked;
}

// The systematic report's front page carries the paper's self-check (#532): one closed table under the citation summary,
// whose summary line says how the ten checks went, with a row for each and its evidence behind a click. The Done card links to
// its id, so the id must be on the page.
async function checkSelfCheck(page, label) {
  const table = page.locator('details#self-check');
  if (!(await table.count())) { problems.push(`${label}: the self-check table (#self-check) is not on the page`); return; }
  const line = ((await table.locator('summary').first().textContent()) ?? '').trim();
  if (!/checked itself: \d+ checks? passed/.test(line)) problems.push(`${label}: the self-check summary line reads "${line}"`);
  if (await table.evaluate(el => el.open)) problems.push(`${label}: the self-check table is open before anyone opened it`);
  await table.locator('summary').first().click(wait);
  const rows = await table.locator('tbody tr').count();
  if (rows !== 10) problems.push(`${label}: the self-check table has ${rows} rows, not 10`);
  if (await table.locator('[class*="rounded"]').count()) problems.push(`${label}: the self-check table has rounded corners`);
  // Open one row's evidence, then close the table again, so the button pass below meets it as a reader first does.
  const evidence = table.locator('tbody details > summary').first();
  await evidence.click(wait);
  if (!(await evidence.evaluate(el => el.parentElement.open))) problems.push(`${label}: the evidence of a self-check row does not open`);
  await table.locator('summary').first().click(wait);
}

async function checkPdf(page, label, path) {
  await page.emulateMedia({ media: 'print' });
  const pdf = await page.pdf({ format: 'A4', printBackground: true, preferCSSPageSize: true });
  await page.emulateMedia({ media: 'screen' });
  if (out) writeFileSync(`${out}/cta-${path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '')}.pdf`, pdf);
  const uris = [...pdf.toString('latin1').matchAll(/\/URI\s*\(((?:\\\)|[^)])*)\)/g)].map(m => m[1].replace(/\\([()\\])/g, '$1'));
  for (const uri of new Set(uris)) {
    if (uri.startsWith(base + '/')) {
      const s = await status(page.request, uri.split('#')[0]);
      if (!(typeof s === 'number' && s >= 200 && s < 300)) problems.push(`${label} (PDF): link ${uri} answered ${s}`);
    } else if (!/^(https?:\/\/|mailto:)/.test(uri) || !external(uri)) problems.push(`${label} (PDF): link "${uri}" is not an absolute web address`);
  }
  if (frontPages.test(path)) {
    const id = path.split('/')[2];
    for (const want of [`${base}${path}`, `${base}/api/workspace/${id}/archive`, `${base}/api/workspace/${id}/protocol.md`])
      if (!uris.includes(want)) problems.push(`${label} (PDF): the front page has no link to ${want}`);
  }
  return uris.length;
}

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
for (const [kind, run] of Object.entries(runs)) {
  for (const owner of [true, false]) {
    const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, permissions: ['clipboard-read', 'clipboard-write'], baseURL: base });
    await context.addInitScript(([id, key]) => {
      window.print = () => { window.__printed = (window.__printed ?? 0) + 1; };
      if (key) localStorage.setItem('traceable-edit-keys', JSON.stringify({ [id]: { k: key, t: Date.now() } }));
    }, [run.id, owner ? run.key : null]);
    for (const path of pagesOf[kind](run.id)) {
      const label = `${path} as ${owner ? 'owner' : 'reader'}`;
      const page = await context.newPage();
      const errors = [];
      page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
      page.on('pageerror', e => errors.push(e.message));
      try {
        const response = await page.goto(base + path, { waitUntil: 'load' });
        if (!response || response.status() >= 400) { problems.push(`${label}: the page answered ${response?.status()}`); continue; }
        await page.waitForTimeout(2000);
        const links = await checkLinks(page, label);
        if (kind === 'systematic' && path.startsWith('/spec-matrix/')) await checkSelfCheck(page, label);
        const buttons = await clickButtons(page, label, owner);
        const pdfLinks = owner ? '-' : await checkPdf(page, label, path);
        await page.waitForTimeout(500);
        errors.forEach(e => problems.push(`${label}: console error: ${e.slice(0, 200)}`));
        console.log(`${label}: ${links} links, ${buttons} buttons, ${pdfLinks} PDF links${errors.length ? `, ${errors.length} console errors` : ''}`);
      } catch (e) {
        problems.push(`${label}: ${e.message.split('\n')[0]}`);
      } finally {
        await page.close();
      }
    }
    await context.close();
  }
}
await browser.close();
if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s):\n` + problems.map(p => '  ' + p).join('\n'));
  process.exit(1);
}
console.log('\nEvery link and button on the report pages works.');

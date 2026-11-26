// Print check (issue #153, "Save as PDF is broken"): prints pages to PDF in Chromium, as "Print or save as PDF" does,
// and fails when a page is cut to one screen instead of flowing over pages, or when the app's frame is printed.
// Used by CI after the accessibility check; run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node print-check.mjs
//
// PRINT_PAGES lists the pages and the least number of A4 pages each must fill, as path=pages
// (default /design/report=3, the made-up report). PRINT_OUT names a folder for the PDFs; CI uploads it. A page with a
// paged report is also checked on screen: A4 sheets with a footer at 1440 px, one continuous sheet at 390 px.
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const targets = (process.env.PRINT_PAGES ?? '/design/report=3').split(',').filter(t => t.length > 0).map(t => {
  const [path, min] = t.split('=');
  return { path, min: Number(min ?? 2) };
});
const out = process.env.PRINT_OUT || null;
if (out) mkdirSync(out, { recursive: true });

// Chromium writes one page object per page; the page tree's /Count says the same.
function pageCount(pdf) {
  const text = pdf.toString('latin1');
  const objects = (text.match(/\/Type\s*\/Page(?![a-zA-Z])/g) ?? []).length;
  const counts = [...text.matchAll(/\/Type\s*\/Pages[^>]*?\/Count\s+(\d+)/g)].map(m => Number(m[1]));
  return Math.max(objects, ...counts, 0);
}

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
let failed = 0;
for (const { path, min } of targets) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const response = await page.goto(base + path, { waitUntil: 'load' });
  if (!response || response.status() >= 400) {
    console.log(`${path}: the page did not load (status ${response ? response.status() : 'none'})`);
    failed++;
    await page.close();
    continue;
  }
  await page.waitForTimeout(1500);
  // On screen a report is A4 sheets with "n of N" at the foot of each (wwwroot/js/paged-preview.js); on a phone it is
  // one continuous sheet.
  const sheets = await page.evaluate(() => {
    const sheet = document.querySelector('[data-paged-sheet]');
    if (!sheet) return null;
    const feet = Array.from(document.querySelectorAll('[data-page-overlay] .paged-foot')).map(f => f.textContent);
    return { paged: sheet.getAttribute('data-paged'), pages: feet.length, first: feet[0] ?? '' };
  });
  if (sheets) {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(600);
    sheets.phone = await page.evaluate(() => document.querySelector('[data-paged-sheet]').getAttribute('data-paged'));
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.waitForTimeout(600);
  }
  await page.emulateMedia({ media: 'print' });
  // On paper the frame drops away: the top bar, the footer and the toolbars are hidden, the report is not.
  const frame = await page.evaluate(() => {
    const shown = el => !!el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
    return {
      header: shown(document.querySelector('nav[aria-label="Main"]')),
      title: shown(document.querySelector('article h1, main h1')),
      clipped: [document.documentElement, document.body, document.querySelector('main')]
        .filter(el => el && getComputedStyle(el).overflowY === 'hidden' && el.scrollHeight > el.clientHeight + 2).length,
    };
  });
  const pdf = await page.pdf({ format: 'A4', printBackground: true });
  const pages = pageCount(pdf);
  const name = path.replace(/[^a-z0-9]+/gi, '-').replace(/^-+|-+$/g, '') || 'landing';
  if (out) writeFileSync(`${out}/${name}-print.pdf`, pdf);
  const problems = [];
  if (pages < min) problems.push(`only ${pages} page(s), at least ${min} expected`);
  if (frame.header) problems.push('the top bar is printed');
  if (!frame.title) problems.push('the title is not printed');
  if (frame.clipped > 0) problems.push(`${frame.clipped} element(s) still cut the content off`);
  if (sheets && (sheets.paged !== 'on' || sheets.pages < 2 || !/^.*1 of \d+/.test(sheets.first))) problems.push(`the screen shows no A4 sheets (${JSON.stringify(sheets)})`);
  if (sheets && sheets.phone !== 'off') problems.push('a phone does not get one continuous sheet');
  console.log(`${path}: ${pages} page(s)${problems.length ? ' - ' + problems.join('; ') : ', prints in full'}`);
  failed += problems.length;
  await page.close();
}
await browser.close();
if (failed > 0) { console.error(`\n${failed} printing problem(s).`); process.exit(1); }
console.log('\nEvery page prints in full.');

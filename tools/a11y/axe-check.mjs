// Accessibility check: opens the public pages in Chromium and runs axe-core (WCAG 2.2 A and AA rules).
// Fails when a page has a "serious" or "critical" violation. Used by CI; run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node axe-check.mjs
//
// A11Y_DARK_PAGES lists pages that are also checked in the dark theme (the choice theme.js keeps in
// localStorage). When A11Y_SCREENSHOTS names a folder, every checked page is also photographed there at phone,
// laptop and large widths, in each theme it was checked in, and a page that scrolls also whole at 1440 px; CI uploads
// the folder as a workflow artifact.
//
// A11Y_WORKERS pages are checked at once (default 4, issue #396), each in its own browser context as before, so a
// page sees nothing of another. The log is printed in the order of the pages, whatever order they finish in.
//
// Every page is opened asking for less motion and checked only once its moving parts are at rest (issue #503). axe
// reads colours as they are drawn, so text caught half-way through a fade failed colour contrast at random, most
// often in the landing page's citation example. No page and no rule is left out; see settle() below.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { mkdirSync, readFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const pages = (process.env.A11Y_PAGES ?? '/,/review,/metrics,/glossary').split(',');
const extraCss = process.env.A11Y_INJECT_CSS ? readFileSync(process.env.A11Y_INJECT_CSS, 'utf8') : null;
const darkPages = (process.env.A11Y_DARK_PAGES ?? '').split(',').filter(p => p.length > 0);
const shots = process.env.A11Y_SCREENSHOTS || null;
const workers = Math.max(1, Number.parseInt(process.env.A11Y_WORKERS ?? '4', 10) || 1);
const widths = [[390, 844], [1440, 900], [1920, 1080]];
if (shots) mkdirSync(shots, { recursive: true });
const runs = pages.map(path => ({ path, theme: 'light' })).concat(darkPages.map(path => ({ path, theme: 'dark' })));

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});

// Checks one page in one theme; returns its log lines and its number of failures.
async function check({ path, theme }) {
  const label = theme === 'dark' ? `${path} (dark)` : path;
  const lines = [];
  const context = await browser.newContext({ reducedMotion: 'reduce' });
  try {
    if (theme === 'dark') await context.addInitScript(() => localStorage.setItem('traceableai-theme', 'dark'));
    const page = await context.newPage();
    const response = await page.goto(base + path, { waitUntil: 'load' });
    // An error page can be perfectly accessible, so a failed page must fail the check by itself.
    if (!response || response.status() >= 400 || (await page.locator('text=An error occurred while handling your request').count()) > 0) {
      lines.push(`\n${label}: the page did not load (status ${response ? response.status() : 'none'})`);
      return { lines, failed: 1 };
    }
    if (extraCss) await page.addStyleTag({ content: extraCss });
    await page.waitForTimeout(1500);
    await settle(page);
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
    const blocking = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
    lines.push(`\n${label}: ${result.violations.length} violation(s), ${blocking.length} serious or critical`);
    for (const v of result.violations) {
      lines.push(`  [${v.impact}] ${v.id}: ${v.help}`);
      for (const node of v.nodes.slice(0, 4)) lines.push(`      ${node.target.join(' ')}`);
    }
    if (shots) await photograph(page, path, theme);
    return { lines, failed: blocking.length };
  } finally {
    await context.close();
  }
}

// Brings every animation and transition to rest before axe looks. The page asks for less motion, so the landing
// page's citation example is already stopped on its first verdict, fully drawn. Anything that still runs to an end
// (a fade-in, a colour transition) is run to that end; anything that repeats for ever and is still running is paused
// where it is, so the measurement and the screenshots see the same frame. Then two frames are drawn.
async function settle(page) {
  await page.evaluate(async () => {
    for (const animation of document.getAnimations()) {
      const end = animation.effect ? animation.effect.getComputedTiming().endTime : Infinity;
      if (Number.isFinite(end)) animation.finish();
      else if (animation.playState === 'running') animation.pause();
    }
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  });
}

async function photograph(page, path, theme) {
  const name = (path.replace(/[^a-z0-9]+/gi, '-').replace(/^-+|-+$/g, '') || 'landing') + '-' + theme;
  for (const [width, height] of widths) {
    await page.setViewportSize({ width, height });
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${shots}/${name}-${width}.png` });
  }
  // The whole page at 1440 px, so long pages can be reviewed (issue #246). The app frame scrolls inside <main>,
  // so it is released as print mode releases it; this is the last step on the page, so nothing after it sees the change.
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addStyleTag({ content: 'html,body,.h-dvh{height:auto!important;overflow:visible!important} #main{overflow:visible!important}' });
  await page.waitForTimeout(300);
  if (await page.evaluate(() => document.documentElement.scrollHeight > window.innerHeight + 2))
    await page.screenshot({ path: `${shots}/${name}-1440-full.png`, fullPage: true });
}

const results = new Array(runs.length);
let next = 0;
let printed = 0;
function flush() {
  while (printed < runs.length && results[printed]) {
    for (const line of results[printed].lines) console.log(line);
    printed++;
  }
}
async function worker() {
  while (next < runs.length) {
    const i = next++;
    results[i] = await check(runs[i]);
    flush();
  }
}
await Promise.all(Array.from({ length: Math.min(workers, runs.length) }, worker));
await browser.close();
const failed = results.reduce((sum, r) => sum + r.failed, 0);
if (failed > 0) { console.error(`\n${failed} serious or critical accessibility problem(s).`); process.exit(1); }
console.log('\nNo serious or critical accessibility problems.');

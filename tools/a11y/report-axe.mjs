// Accessibility check of the report pages of finished runs (issue #624). axe-check.mjs reads pages with made-up data;
// this one opens the report pages of the fake runs CtaSeedTests seeds (CTA_KEYS), as a reader, in the light and the dark
// theme, and runs axe-core (WCAG 2.2 A and AA rules) three times on each: as the page opens, with its folded sections
// opened, and with a citation's card open, since the card is what a reader opens most and is drawn only on demand.
// Fails on any "serious" or "critical" violation. Run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 CTA_KEYS=/path/keys.json node report-axe.mjs
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { readFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const runs = JSON.parse(readFileSync(process.env.CTA_KEYS ?? 'cta-keys.json', 'utf8'));
const pagesOf = {
  systematic: id => [`/spec-matrix/${id}`, `/review/${id}`],
  multivocal: id => [`/mlr-paper/${id}`, `/mlr-report/${id}`],
};
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const wait = { timeout: 10000 };

// The same rest as axe-check.mjs (issue #503): finite animations run to their end, endless ones pause, two frames drawn.
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

async function measure(page, label, lines) {
  await settle(page);
  const result = await new AxeBuilder({ page }).withTags(tags).analyze();
  const blocking = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  lines.push(`${label}: ${result.violations.length} violation(s), ${blocking.length} serious or critical`);
  for (const v of result.violations) {
    lines.push(`  [${v.impact}] ${v.id}: ${v.help}`);
    for (const node of v.nodes.slice(0, 4)) lines.push(`      ${node.target.join(' ')}`);
  }
  return blocking.length;
}

// Opens every folded section, then measures; then opens the first citation's card and measures with it open.
// A page without citations is measured once more as it is, so the count of measurements does not depend on the run.
async function check(browser, path, theme) {
  const label = theme === 'dark' ? `${path} (dark)` : path;
  const lines = [];
  let failed = 0;
  const context = await browser.newContext({ reducedMotion: 'reduce', viewport: { width: 1440, height: 900 } });
  try {
    if (theme === 'dark') await context.addInitScript(() => localStorage.setItem('traceableai-theme', 'dark'));
    const page = await context.newPage();
    const response = await page.goto(base + path, { waitUntil: 'load' });
    if (!response || response.status() >= 400) return { lines: [`${label}: the page did not load (status ${response?.status() ?? 'none'})`], failed: 1 };
    await page.waitForTimeout(1500);
    failed += await measure(page, `${label}, as opened`, lines);
    for (const summary of await page.locator('details:not([open]) > summary:visible').all()) await summary.click(wait).catch(() => {});
    failed += await measure(page, `${label}, sections open`, lines);
    const cite = page.locator('button[id^="cite-"]').first();
    if (await cite.count()) {
      await cite.scrollIntoViewIfNeeded();
      await cite.click(wait);
      await page.getByRole('button', { name: 'Close', exact: true }).first().waitFor({ state: 'visible', ...wait });
      failed += await measure(page, `${label}, citation card open`, lines);
    } else lines.push(`${label}: no citation to open`);
  } catch (e) {
    lines.push(`${label}: ${e.message.split('\n')[0]}`);
    failed++;
  } finally {
    await context.close();
  }
  return { lines, failed };
}

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
let failed = 0;
for (const [kind, run] of Object.entries(runs)) {
  if (!pagesOf[kind]) continue;
  for (const path of pagesOf[kind](run.id)) {
    for (const theme of ['light', 'dark']) {
      const result = await check(browser, path, theme);
      result.lines.forEach(line => console.log(line));
      failed += result.failed;
    }
  }
}
await browser.close();
if (failed > 0) { console.error(`\n${failed} serious or critical accessibility problem(s) on the report pages.`); process.exit(1); }
console.log('\nNo serious or critical accessibility problems on the report pages.');

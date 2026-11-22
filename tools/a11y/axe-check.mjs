// Accessibility check: opens the public pages in Chromium and runs axe-core (WCAG 2.2 A and AA rules).
// Fails when a page has a "serious" or "critical" violation. Used by CI; run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node axe-check.mjs
//
// A11Y_DARK_PAGES lists pages that are also checked in the dark theme (the choice theme.js keeps in
// localStorage). When A11Y_SCREENSHOTS names a folder, every checked page is also photographed there at phone,
// laptop and large widths, in each theme it was checked in, and a page that scrolls also whole at 1440 px; CI uploads
// the folder as a workflow artifact.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { mkdirSync, readFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const pages = (process.env.A11Y_PAGES ?? '/,/review,/metrics,/glossary').split(',');
const extraCss = process.env.A11Y_INJECT_CSS ? readFileSync(process.env.A11Y_INJECT_CSS, 'utf8') : null;
const darkPages = (process.env.A11Y_DARK_PAGES ?? '').split(',').filter(p => p.length > 0);
const shots = process.env.A11Y_SCREENSHOTS || null;
const widths = [[390, 844], [1440, 900], [1920, 1080]];
if (shots) mkdirSync(shots, { recursive: true });
const runs = pages.map(path => ({ path, theme: 'light' })).concat(darkPages.map(path => ({ path, theme: 'dark' })));

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
let failed = 0;
for (const { path, theme } of runs) {
  const label = theme === 'dark' ? `${path} (dark)` : path;
  const context = await browser.newContext();
  if (theme === 'dark') await context.addInitScript(() => localStorage.setItem('traceableai-theme', 'dark'));
  const page = await context.newPage();
  const response = await page.goto(base + path, { waitUntil: 'load' });
  // An error page can be perfectly accessible, so a failed page must fail the check by itself.
  if (!response || response.status() >= 400 || (await page.locator('text=An error occurred while handling your request').count()) > 0) {
    console.log(`\n${label}: the page did not load (status ${response ? response.status() : 'none'})`);
    failed++;
    await context.close();
    continue;
  }
  if (extraCss) await page.addStyleTag({ content: extraCss });
  await page.waitForTimeout(1500);
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const blocking = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  console.log(`\n${label}: ${result.violations.length} violation(s), ${blocking.length} serious or critical`);
  for (const v of result.violations) {
    console.log(`  [${v.impact}] ${v.id}: ${v.help}`);
    for (const node of v.nodes.slice(0, 4)) console.log(`      ${node.target.join(' ')}`);
  }
  failed += blocking.length;
  if (shots) {
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
  await context.close();
}
await browser.close();
if (failed > 0) { console.error(`\n${failed} serious or critical accessibility problem(s).`); process.exit(1); }
console.log('\nNo serious or critical accessibility problems.');

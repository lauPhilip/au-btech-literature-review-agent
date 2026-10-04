// Accessibility check: opens the public pages in Chromium and runs axe-core (WCAG 2.2 A and AA rules).
// Fails when a page has a "serious" or "critical" violation. Used by CI; run it locally with
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node axe-check.mjs
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { readFileSync } from 'node:fs';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const pages = (process.env.A11Y_PAGES ?? '/,/review,/metrics,/glossary').split(',');
const extraCss = process.env.A11Y_INJECT_CSS ? readFileSync(process.env.A11Y_INJECT_CSS, 'utf8') : null;

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
let failed = 0;
for (const path of pages) {
  const context = await browser.newContext();
  const page = await context.newPage();
  const response = await page.goto(base + path, { waitUntil: 'load' });
  // An error page can be perfectly accessible, so a failed page must fail the check by itself.
  if (!response || response.status() >= 400 || (await page.locator('text=An error occurred while handling your request').count()) > 0) {
    console.log(`\n${path}: the page did not load (status ${response ? response.status() : 'none'})`);
    failed++;
    await context.close();
    continue;
  }
  if (extraCss) await page.addStyleTag({ content: extraCss });
  await page.waitForTimeout(1500);
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const blocking = result.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
  console.log(`\n${path}: ${result.violations.length} violation(s), ${blocking.length} serious or critical`);
  for (const v of result.violations) {
    console.log(`  [${v.impact}] ${v.id}: ${v.help}`);
    for (const node of v.nodes.slice(0, 4)) console.log(`      ${node.target.join(' ')}`);
  }
  failed += blocking.length;
  await context.close();
}
await browser.close();
if (failed > 0) { console.error(`\n${failed} serious or critical accessibility problem(s).`); process.exit(1); }
console.log('\nNo serious or critical accessibility problems.');

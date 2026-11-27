// New review on one laptop screen (issue #728): at 1366 × 768 and 1440 × 900 the composer and the criteria panel
// (objective, query, criteria, visual summary, options and Start) should show without scrolling. The check opens
// /review twice per size, empty and with "Try an example" filled in, and prints how far the page scrolls (the frame's
// <main> is the only element that scrolls) and how tall the criteria panel is. The new controls of the form, the
// database chips and the visual summary choice, must be at least 44 px high where the page has them.
// FIT_STRICT=1 fails the check when either size scrolls. FIT_SHOTS=1 prints a JPEG of each screen as base64 between
// markers, because the workflow's screenshot artifact cannot be downloaded from where pull requests are reviewed.
// FIT_HTML=1 also prints the page's HTML, gzipped and in base64, so a layout can be tried out on a static copy.
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node fit-check.mjs
import { chromium } from 'playwright';
import { gzipSync } from 'node:zlib';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const strict = process.env.FIT_STRICT === '1';
const shots = process.env.FIT_SHOTS === '1';
const dumpHtml = process.env.FIT_HTML === '1';
const wait = { timeout: 10000 };
const sizes = [{ width: 1366, height: 768 }, { width: 1440, height: 900 }];

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const failures = [];

for (const viewport of sizes) {
  for (const example of [false, true]) {
    const context = await browser.newContext({ viewport });
    const page = await context.newPage();
    await page.goto(base + '/review', { waitUntil: 'load' });
    // Interactive pages need their live connection before a click does anything.
    await page.waitForTimeout(1500);
    // Without a server key the page opens the API keys dialog by itself.
    if (await page.locator('#key-dialog').isVisible()) {
      await page.keyboard.press('Escape');
      await page.locator('#key-dialog').waitFor({ state: 'hidden', ...wait });
    }
    if (example) {
      await page.getByRole('button', { name: 'Try an example' }).click(wait);
      await page.waitForFunction(() => (document.querySelector('#f-objective')?.value ?? '').length > 20, null, wait);
      await page.waitForTimeout(300);
    }
    const m = await page.evaluate(() => {
      const main = document.querySelector('main#main');
      const height = sel => Math.round(document.querySelector(sel)?.getBoundingClientRect().height ?? 0);
      const small = [...document.querySelectorAll('[data-fit-control]')]
        .filter(el => el.offsetParent !== null && el.getBoundingClientRect().height < 44)
        .map(el => `${el.textContent.trim()} (${Math.round(el.getBoundingClientRect().height)} px)`);
      return { scroll: main.scrollHeight, client: main.clientHeight, criteria: height('#criteria'), composer: height('.ui-card'), small };
    });
    const over = m.scroll - m.client;
    const label = `${viewport.width} × ${viewport.height}${example ? ', example' : ', empty'}`;
    console.log(`${label}: main scrollHeight ${m.scroll}, clientHeight ${m.client}, scrolls ${Math.max(0, over)} px; ` +
      `criteria panel ${m.criteria} px; composer card ${m.composer} px`);
    if (m.small.length) failures.push(`${label}: controls under 44 px: ${m.small.join(', ')}`);
    if (strict && over > 0) failures.push(`${label}: the page scrolls ${over} px`);
    if (shots) {
      const jpeg = await page.screenshot({ type: 'jpeg', quality: 55 });
      console.log(`FIT-SHOT-BEGIN ${viewport.width}x${viewport.height}-${example ? 'example' : 'empty'}\n${jpeg.toString('base64')}\nFIT-SHOT-END`);
    }
    if (dumpHtml && example) {
      const html = gzipSync(Buffer.from(await page.content())).toString('base64');
      console.log(`FIT-HTML-BEGIN ${viewport.width}x${viewport.height}\n${html}\nFIT-HTML-END`);
    }
    await context.close();
  }
}

await browser.close();
if (failures.length) {
  failures.forEach(f => console.error(f));
  process.exit(1);
}
console.log(strict ? 'New review fits one screen at every size.' : 'Measured (FIT_STRICT is off, so scrolling is reported, not failed).');

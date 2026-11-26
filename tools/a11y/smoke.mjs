// Smoke test (issue #312): opens every main page and clicks through the flows a visitor uses, on the real pages and
// the sample pages of the app in Development, so a page that does not load, a live connection that never starts or a
// button that does nothing fails the pull request. The accessibility job runs it after axe-core:
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node smoke.mjs
// Any console error or uncaught page error on a page fails it too.
import { chromium } from 'playwright';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const pages = ['/', '/start', '/review', '/metrics', '/about', '/model-card', '/privacy', '/glossary', '/verify', '/docs/api',
  '/design/run', '/design/run?state=done', '/design/report', '/design/metrics'];
const wait = { timeout: 10000 };

// The flows: each must change the page as a visitor expects. A step that throws fails the check with its name.
const flows = [
  {
    name: 'New review: the key dialog closes and "Try an example" fills the form',
    path: '/review',
    async run(page) {
      // Without a server key, as here, the page opens the API keys dialog by itself; Escape must close it.
      if (await page.locator('#key-dialog').isVisible()) {
        await page.keyboard.press('Escape');
        await page.locator('#key-dialog').waitFor({ state: 'hidden', ...wait });
      }
      await page.getByRole('button', { name: 'Try an example' }).click(wait);
      await page.waitForFunction(() => (document.querySelector('#f-objective')?.value ?? '').length > 20, null, wait);
    },
  },
  {
    name: 'Review output: a citation opens its card, and Close closes it',
    path: '/design/report',
    async run(page) {
      const chip = page.locator('button[id^="cite-"]').first();
      await chip.scrollIntoViewIfNeeded();
      await chip.click(wait);
      const close = page.getByRole('button', { name: 'Close', exact: true }).first();
      await close.waitFor({ state: 'visible', ...wait });
      await close.click(wait);
      await close.waitFor({ state: 'hidden', ...wait });
    },
  },
  {
    // The sample run has no edit key, so this browser may edit it (issue #413). Cancel makes no change.
    name: 'Report: "Edit the text" opens a sentence\'s edit box and Cancel closes it',
    path: '/design/report',
    async run(page) {
      await page.getByRole('button', { name: 'Edit the text', exact: true }).click(wait);
      const edit = page.locator('button[id^="edit-discussionItem-"]').first();
      await edit.scrollIntoViewIfNeeded();
      await edit.click(wait);
      const box = page.locator('#sentence-editor');
      await box.waitFor({ state: 'visible', ...wait });
      await box.getByRole('button', { name: 'Cancel', exact: true }).click(wait);
      await box.waitFor({ state: 'hidden', ...wait });
    },
  },
  {
    name: 'Glossary: the search narrows the list',
    path: '/glossary',
    async run(page) {
      const count = page.locator('#glossary-count');
      const before = (await count.textContent())?.trim();
      await page.locator('#glossary-search').fill('kappa');
      await page.waitForFunction(b => document.querySelector('#glossary-count')?.textContent?.trim() !== b, before, wait);
    },
  },
  {
    // The page has no live connection: the copy button is handled by review-output.js. Headless Chrome may refuse
    // the clipboard, so either answer counts; a button that does nothing does not.
    name: 'API page: a Copy button answers',
    path: '/docs/api',
    async run(page) {
      const button = page.locator('[data-copy="start-request"]');
      await button.click(wait);
      await page.waitForFunction(() => document.querySelector('[data-copy="start-request"]')?.textContent?.trim() !== 'Copy', null, wait);
    },
  },
  {
    name: 'The theme switch turns the page dark and back',
    path: '/about',
    async run(page) {
      const toggle = page.locator('[data-theme-toggle]').first();
      const theme = () => page.evaluate(() => document.documentElement.getAttribute('data-theme') ?? 'light');
      const first = await theme();
      await toggle.click(wait);
      await page.waitForFunction(f => (document.documentElement.getAttribute('data-theme') ?? 'light') !== f, first, wait);
      await toggle.click(wait);
      if ((await theme()) !== first) throw new Error('the second click did not switch back');
    },
  },
];

// The paged report (wwwroot/js/paged-preview.js, issues #671 and #672), at the width where a sidebar and a narrow
// sheet used to give a scroll bar (809 px) and on a wide screen. SMOKE_OUT names a folder for screenshots.
const shot = async (page, name) => { if (process.env.SMOKE_OUT) await page.screenshot({ path: `${process.env.SMOKE_OUT}/${name}.png` }); };
const inView = (page, selector) => page.evaluate(sel => {
  const r = document.querySelector(sel)?.getBoundingClientRect();
  return !!r && r.top >= 0 && r.bottom <= window.innerHeight && r.height > 0;
}, selector);
for (const width of [809, 1440]) {
  const viewport = { width, height: 900 };
  flows.push({
    name: `Paged report at ${width} px: no sideways scroll`,
    path: '/design/report',
    viewport,
    async run(page) {
      await page.waitForSelector('[data-paged-sheet][data-paged="on"]', wait);
      const { sw, cw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
      if (sw > cw) throw new Error(`the page is ${sw} px wide in a ${cw} px window`);
      const boxes = await page.evaluate(() => [...document.querySelectorAll('[data-paged-sheet] *')]
        .filter(e => e.scrollWidth > e.clientWidth + 1 && ['auto', 'scroll'].includes(getComputedStyle(e).overflowX) && e.clientWidth > 0)
        .map(e => `${e.tagName}.${String(e.className).slice(0, 40)} ${e.scrollWidth}>${e.clientWidth}`));
      if (boxes.length) throw new Error(`a box on the sheet scrolls sideways: ${boxes[0]}`);
    },
  });
  flows.push({
    name: `Paged report at ${width} px: no heading ends a sheet`,
    path: '/design/report',
    viewport,
    async run(page) {
      await page.waitForSelector('[data-paged-sheet][data-paged="on"]', wait);
      await page.waitForTimeout(600);
      const stranded = await page.evaluate(() => {
        const sheet = document.querySelector('[data-paged-sheet]');
        const pages = [...document.querySelectorAll('[data-page-overlay] .paged-page')].map(p => p.getBoundingClientRect());
        const blocks = [...sheet.querySelectorAll('p, h1, h2, h3, h4, h5, li, tr, figure, table, pre, blockquote')].filter(e => e.getClientRects().length > 0);
        const bad = [];
        blocks.forEach((el, i) => {
          if (!/^H[1-4]$/.test(el.tagName) || el.closest('[data-front-page]')) return;
          const next = blocks.slice(i + 1).find(n => !el.contains(n));
          const page = pages.find(p => el.getBoundingClientRect().top >= p.top - 1 && el.getBoundingClientRect().top < p.bottom);
          if (next && page && next.getBoundingClientRect().top >= page.bottom - 1) bad.push(el.textContent.trim().slice(0, 40));
        });
        return bad;
      });
      if (stranded.length) throw new Error(`heading at the foot of a sheet: ${stranded.join(' | ')}`);
    },
  });
  flows.push({
    name: `Paged report at ${width} px: a citation's card opens in view`,
    path: '/design/report',
    viewport,
    async run(page) {
      await page.waitForSelector('[data-paged-sheet][data-paged="on"]', wait);
      const open = async (what, click) => {
        await click();
        await page.locator('[data-citation-panel]').first().waitFor({ state: 'visible', ...wait });
        await page.waitForTimeout(900); // paging settles (200 ms) and the scroll ends
        if (!(await inView(page, '[data-citation-panel]'))) throw new Error(`${what}: the card is not in the window`);
        await shot(page, `paged-card-${width}-${what.replace(/\W+/g, '-')}`);
        await page.getByRole('button', { name: 'Close', exact: true }).first().click(wait);
        await page.locator('[data-citation-panel]').first().waitFor({ state: 'hidden', ...wait });
      };
      const chips = page.locator('button[id^="cite-"]');
      for (const n of [0, Math.min(3, (await chips.count()) - 1)]) {
        await open(`citation ${n + 1}`, async () => { await chips.nth(n).scrollIntoViewIfNeeded(); await chips.nth(n).click(wait); });
      }
      const first = page.getByRole('button', { name: 'Show the first', exact: true });
      if (await first.count()) await open('show the first', () => first.click(wait));
    },
  });
}

// Screens that must fit a 390 x 844 phone without scrolling (issue #244), in light and dark.
const phoneScreens = ['/', '/review', '/design/run', '/design/run?state=done'];

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const failures = [];

async function open(path, viewport = { width: 1280, height: 800 }) {
  const context = await browser.newContext({ viewport });
  const page = await context.newPage();
  const errors = [];
  page.on('console', m => { if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) errors.push(m.text()); });
  page.on('pageerror', e => errors.push(e.message));
  // The browser's own "Failed to load resource" line has no address, so a failed request is reported with it.
  // .NET's development tooling asks for hot-reload settings that only exist under `dotnet watch`; production never does.
  page.on('response', r => {
    const path = new URL(r.url()).pathname;
    if (r.status() >= 400 && !path.startsWith('/_framework/dotnet-browser-tools/')) errors.push(`${r.status()} ${path}`);
  });
  const response = await page.goto(base + path, { waitUntil: 'load' });
  // Interactive pages need their live connection before a click does anything.
  await page.waitForTimeout(1500);
  return { context, page, errors, status: response ? response.status() : 0 };
}

for (const path of pages) {
  const { context, page, errors, status } = await open(path);
  const problems = [];
  if (status === 0 || status >= 400) problems.push(`answered ${status}`);
  if ((await page.locator('text=An error occurred while handling your request').count()) > 0) problems.push('shows the error page');
  if ((await page.locator('h1').count()) === 0) problems.push('has no heading');
  problems.push(...errors.map(e => `console: ${e.split('\n')[0]}`));
  console.log(`${problems.length === 0 ? 'ok  ' : 'FAIL'} ${path}${problems.length ? ': ' + problems.join('; ') : ''}`);
  if (problems.length) failures.push(path);
  await context.close();
}

for (const flow of flows) {
  const { context, page, errors } = await open(flow.path, flow.viewport);
  let problem = null;
  try {
    await flow.run(page);
    if (errors.length) problem = `console: ${errors[0].split('\n')[0]}`;
  } catch (error) {
    problem = error.message.split('\n')[0];
  }
  console.log(`${problem ? 'FAIL' : 'ok  '} ${flow.name}${problem ? ': ' + problem : ''}`);
  if (problem) failures.push(flow.name);
  await context.close();
}

for (const theme of ['light', 'dark']) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  if (theme === 'dark') await context.addInitScript(() => localStorage.setItem('traceableai-theme', 'dark'));
  for (const path of phoneScreens) {
    const page = await context.newPage();
    await page.goto(base + path, { waitUntil: 'load' });
    await page.waitForTimeout(1500);
    if (await page.locator('#key-dialog').isVisible()) await page.keyboard.press('Escape');
    // The app frame scrolls inside <main>; the landing page scrolls the document.
    const over = await page.evaluate(() => {
      const el = document.querySelector('#main') ?? document.scrollingElement;
      return el.scrollHeight - el.clientHeight;
    });
    const ok = over <= 2;
    console.log(`${ok ? 'ok  ' : 'FAIL'} ${path} fits a phone (${theme})${ok ? '' : `: ${over} px too tall`}`);
    if (!ok) failures.push(`${path} on a phone (${theme})`);
    await page.close();
  }
  await context.close();
}

await browser.close();
if (failures.length) {
  console.log(`\n${failures.length} smoke check(s) failed.`);
  process.exit(1);
}
console.log(`\nAll ${pages.length} pages and ${flows.length} flows work, and ${phoneScreens.length} screens fit a phone in both themes.`);

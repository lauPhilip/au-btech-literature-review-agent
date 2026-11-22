// Smoke test (issue #312): opens every main page and clicks through the flows a visitor uses, on the real pages and
// the sample pages of the app in Development, so a page that does not load, a live connection that never starts or a
// button that does nothing fails the pull request. The accessibility job runs it after axe-core:
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node smoke.mjs
// Any console error or uncaught page error on a page fails it too.
import { chromium } from 'playwright';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const pages = ['/', '/start', '/review', '/metrics', '/about', '/model-card', '/privacy', '/glossary', '/verify',
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

// Screens that must fit a 390 x 844 phone without scrolling (issue #244), in light and dark.
const phoneScreens = ['/', '/review', '/design/run'];

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const failures = [];

async function open(path) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
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
  const { context, page, errors } = await open(flow.path);
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

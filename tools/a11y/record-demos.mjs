// Records the demo GIFs (issue #259): one scene of 6 seconds per thing the site does, from the app running in
// Development with its made-up sample pages (/design/run, /design/report, /design/metrics), so no model, key or real
// run is needed. Each scene is filmed by Playwright at 1280 x 720 and turned into a GIF by ffmpeg: the last 6 s,
// 12 frames a second, 960 px wide (720 for the busiest scene), with its own palette, to stay under about 2 MB.
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 DEMO_OUT=../../demo-gifs node record-demos.mjs
// DEMO_SCENES picks scenes by name (comma-separated); all by default. A scene that fails is reported and skipped.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readdirSync, renameSync, rmSync, statSync } from 'node:fs';
import { join } from 'node:path';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const out = process.env.DEMO_OUT ?? 'demo-gifs';
const only = (process.env.DEMO_SCENES ?? '').split(',').filter(s => s.length > 0);
const size = { width: 1280, height: 720 };
const seconds = 6;

const pause = (page, ms) => page.waitForTimeout(ms);

async function scroll(page, pixels, ms) {
  // The page scrolls inside the frame, not the window, so the wheel has to turn over the content.
  await page.mouse.move(size.width / 2, size.height * 0.7);
  const steps = 24;
  for (let i = 0; i < steps; i++) {
    await page.mouse.wheel(0, pixels / steps);
    await pause(page, ms / steps);
  }
}

// Each scene opens its page, waits for it to settle, and then acts for at least `seconds`; only the last
// `seconds` of the film become the GIF, so the page load is cut away.
const scenes = [
  {
    name: 'citation-check',
    alt: 'The landing page example: three citations are checked one by one and marked supported, partly supported and not supported.',
    async run(page) {
      await page.goto(base + '/');
      await pause(page, 7500);
    },
  },
  {
    name: 'new-review',
    alt: 'New review: a question is typed in, then the worked example fills the form, with the criteria beside it.',
    async run(page) {
      await page.goto(base + '/review');
      await pause(page, 1500);
      // Without a server key (as in CI) the page opens the API keys dialog by itself; Escape closes it.
      if (await page.locator('#key-dialog').isVisible()) await page.keyboard.press('Escape');
      await pause(page, 500);
      await page.locator('#f-objective').click();
      await page.locator('#f-objective').pressSequentially('How do chatbots help people stop smoking?', { delay: 70 });
      await pause(page, 1300);
      await page.getByRole('button', { name: 'Try an example' }).click({ timeout: 5000 });
      await pause(page, 2200);
    },
  },
  {
    name: 'running',
    alt: 'A running review: its seven steps with the one in progress, and then the finished run with its results.',
    async run(page) {
      await page.goto(base + '/design/run');
      await pause(page, 4200);
      await page.goto(base + '/design/run?state=done');
      await pause(page, 3300);
    },
  },
  {
    name: 'report',
    width: 720, // the open citation card changes much of the screen; a smaller GIF keeps it under 2 MB
    alt: 'The report: a citation is opened to show its verdict and the quote from the paper, then the next flagged citation.',
    async run(page) {
      await page.goto(base + '/design/report');
      await pause(page, 1500);
      const chip = page.locator('button[id^="cite-"]').first();
      await chip.scrollIntoViewIfNeeded();
      await pause(page, 700);
      await chip.click();
      await pause(page, 2300);
      const next = page.getByRole('button', { name: /^Next( flagged)? citation|^Next$|^Show the first$/ });
      if (await next.count() > 0) await next.first().click();
      await pause(page, 2500);
    },
  },
  {
    name: 'metrics',
    alt: 'Metrics: one bar of citation verdicts per review, then the tables of shares and counts per run.',
    async run(page) {
      await page.goto(base + '/design/metrics');
      await pause(page, 2500);
      await scroll(page, 900, 3500);
      await pause(page, 1500);
    },
  },
  {
    name: 'glossary-and-dark',
    alt: 'The glossary is searched for "kappa", and then the site switches to its dark theme.',
    async run(page) {
      await page.goto(base + '/glossary');
      await pause(page, 1500);
      await page.locator('#glossary-search').click();
      await page.locator('#glossary-search').pressSequentially('kappa', { delay: 180 });
      await pause(page, 2000);
      await page.locator('[data-theme-toggle]').first().click();
      await pause(page, 2700);
    },
  },
];

function toGif(video, gif, width) {
  const filter = `fps=12,scale=${width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];` +
    '[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle';
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-sseof', `-${seconds}`, '-i', video, '-vf', filter, '-loop', '0', gif]);
}

mkdirSync(out, { recursive: true });
const films = join(out, 'films');
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
let made = 0;
for (const scene of scenes.filter(s => only.length === 0 || only.includes(s.name))) {
  const dir = join(films, scene.name);
  rmSync(dir, { recursive: true, force: true });
  const context = await browser.newContext({ viewport: size, recordVideo: { dir, size }, reducedMotion: 'no-preference' });
  const page = await context.newPage();
  try {
    await scene.run(page);
  } catch (error) {
    console.log(`${scene.name}: the scene failed (${error.message.split('\n')[0]})`);
    await context.close();
    continue;
  }
  await context.close(); // the film is written when its context closes
  const video = readdirSync(dir).find(f => f.endsWith('.webm'));
  const gif = join(out, `${scene.name}.gif`);
  toGif(join(dir, video), gif, scene.width ?? 960);
  renameSync(join(dir, video), join(out, `${scene.name}.webm`));
  console.log(`${scene.name}: ${gif}, ${(statSync(gif).size / 1048576).toFixed(2)} MB. Alt text: ${scene.alt}`);
  made++;
}
await browser.close();
rmSync(films, { recursive: true, force: true });
if (made === 0) process.exit(1);

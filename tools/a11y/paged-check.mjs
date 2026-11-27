// The paged report's three layout faults of issue #737, checked on the sample report (/design/report) with extra text
// put into its sheet, because the sample is too short to show them: sections whose paragraphs run from a few lines
// to longer than a page, one just shorter than a page under its heading, and a diagram taller than a page. The pager (wwwroot/js/paged-preview.js and paged-fit.js)
// lays the sheet out again when its content changes. Then, at three widths:
//   1. a heading keeps at least two lines of its text on its page (no heading alone at the top or foot of a page);
//   2. no line of text reaches a page's foot margin, where the footer is, or runs past the page;
//   3. no diagram crosses the edge of a page.
// The check also makes sure the text did need a paragraph broken and a figure scaled, so it never passes by default.
//   cd tools/a11y && npm ci && BASE_URL=http://localhost:5038 node paged-check.mjs
import { chromium } from 'playwright';

const base = (process.env.BASE_URL ?? 'http://localhost:5038').replace(/\/$/, '');
const wait = { timeout: 15000 };

// Puts the extra sections at the end of the sheet. Words come from a fixed list in a fixed order, so every run is the same.
function addText() {
  const words = 'agents review evidence model studies tools quality appraisal search records screened included bias data'.split(' ');
  let seed = 7;
  const next = () => (seed = (seed * 1103515245 + 12345) % 2147483648);
  const element = (tag, className, text) => { const el = document.createElement(tag); el.className = className; if (text) el.textContent = text; return el; };
  const paragraph = n => element('p', 'text-ink', Array.from({ length: n }, () => words[next() % words.length]).join(' ') + '.');
  const sizes = [30, 520, 80, 600, 150, 900, 250, 400, 60, 560, 120, 480];
  const holder = element('div', 'space-y-10 pt-8');
  holder.setAttribute('data-paged-check', '');
  for (let s = 0; s < 4; s++) {
    const section = element('section', 'space-y-5');
    section.appendChild(element('h2', 'font-display text-xl font-semibold text-heading', `${8 + s}. Extra section`));
    for (let k = 0; k < 4; k++) {
      const part = element('div', 'space-y-2');
      part.appendChild(element('h3', 'font-display text-base font-semibold text-heading', `${8 + s}.${k + 1} Extra part`));
      part.appendChild(paragraph(sizes[(s * 4 + k) % sizes.length]));
      if (k % 2 === 1) part.appendChild(paragraph(sizes[(s * 4 + k + 5) % sizes.length]));
      section.appendChild(part);
    }
    if (s === 1) {
      // A concept map as tall as Mermaid draws a long top-down chart.
      const box = element('div', 'mt-5 border border-line p-3 space-y-2');
      box.appendChild(element('p', 'text-[13px] text-muted', 'A concept map taller than a page.'));
      const fill = element('div', 'w-full flex flex-col items-center bg-soft p-3');
      const ns = 'http://www.w3.org/2000/svg';
      const svg = document.createElementNS(ns, 'svg');
      svg.setAttribute('viewBox', '0 0 400 1800');
      svg.setAttribute('width', '100%');
      svg.setAttribute('role', 'img');
      svg.setAttribute('aria-label', 'Test diagram');
      svg.style.maxWidth = '400px';
      const rect = document.createElementNS(ns, 'rect');
      ['x', 'y', 'width', 'height'].forEach((a, i) => rect.setAttribute(a, String([10, 10, 380, 1780][i])));
      rect.setAttribute('fill', 'none');
      rect.setAttribute('stroke', 'currentColor');
      svg.appendChild(rect);
      fill.appendChild(svg);
      box.appendChild(fill);
      section.appendChild(box);
    }
    holder.appendChild(section);
  }
  // A heading over a paragraph just shorter than a page: together they do not fit, which left the heading alone.
  const near = element('div', 'space-y-2');
  near.appendChild(element('h3', 'font-display text-base font-semibold text-heading', '12.1 Extra part'));
  const long = near.appendChild(paragraph(40));
  holder.lastChild.appendChild(near);
  const sheet = document.querySelector('[data-paged-sheet]');
  sheet.appendChild(holder);
  const page = document.querySelector('[data-page-overlay] .paged-page').getBoundingClientRect().height;
  const body = page - 2 * parseFloat(sheet.style.getPropertyValue('--sheet-pad'));
  while (long.getBoundingClientRect().height < body - 12) long.textContent = words[next() % words.length] + ' ' + long.textContent;
}

// The three faults, read from the laid-out sheet.
function faults() {
  const sheet = document.querySelector('[data-paged-sheet]');
  const pages = [...document.querySelectorAll('[data-page-overlay] .paged-page')].map(p => p.getBoundingClientRect());
  const pad = parseFloat(sheet.style.getPropertyValue('--sheet-pad'));
  const gap = pages.length > 1 ? pages[1].top - pages[0].bottom : 0;
  const pageOf = y => pages.findIndex(p => y >= p.top - 1 && y < p.bottom + gap);
  const lines = el => { const r = document.createRange(); r.selectNodeContents(el); return [...r.getClientRects()].filter(l => l.height > 0 && l.width > 0); };
  const name = el => el.textContent.trim().slice(0, 40);
  const added = sheet.querySelector('[data-paged-check]');
  const out = { heading: [], foot: [], diagram: [], split: added.querySelectorAll('[data-page-split]').length, fit: added.querySelectorAll('[data-page-fit]').length };
  added.querySelectorAll('h2, h3').forEach(h => {
    const after = [...added.querySelectorAll('h2, h3, p')];
    const text = after.slice(after.indexOf(h) + 1).find(e => e.tagName === 'P');
    if (!text) return;
    const second = lines(text)[1] ?? lines(text)[0];
    if (pageOf(lines(h)[0].top) !== pageOf(second.top)) out.heading.push(name(h));
  });
  added.querySelectorAll('h2, h3, p').forEach(el => {
    const over = lines(el).find(l => { const p = pages[pageOf(l.top)]; return p && l.bottom > p.bottom - pad + 2; });
    if (over) out.foot.push(name(el));
  });
  added.querySelectorAll('svg').forEach(s => {
    const r = s.getBoundingClientRect();
    if (pageOf(r.top + 2) !== pageOf(r.bottom - 2)) out.diagram.push(`${Math.round(r.height)} px tall`);
  });
  return out;
}

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const failures = [];
for (const width of [809, 1100, 1440]) {
  const page = await browser.newPage({ viewport: { width, height: 900 } });
  await page.goto(base + '/design/report', { waitUntil: 'load' });
  await page.waitForSelector('[data-paged-sheet][data-paged="on"]', wait);
  await page.evaluate(addText);
  await page.waitForTimeout(1500); // the pager waits 200 ms after a change, then lays the sheet out again
  const found = await page.evaluate(faults);
  const checks = [
    ['a heading keeps two lines of its text on its page', found.heading],
    ['no text reaches the footer', found.foot],
    ['no diagram crosses the edge of a page', found.diagram],
    ['a paragraph was broken and a diagram scaled', found.split > 0 && found.fit > 0 ? [] : [`${found.split} broken, ${found.fit} scaled`]],
  ];
  for (const [what, bad] of checks) {
    console.log(`${bad.length ? 'FAIL' : 'ok  '} Paged report at ${width} px: ${what}${bad.length ? ': ' + bad.join(' | ') : ''}`);
    if (bad.length) failures.push(`${what} at ${width} px`);
  }
  await page.close();
}
await browser.close();
if (failures.length) {
  console.log(`\n${failures.length} paged report check(s) failed.`);
  process.exit(1);
}
console.log('\nThe paged report keeps headings with their text, its text off the footer and its diagrams on one page.');

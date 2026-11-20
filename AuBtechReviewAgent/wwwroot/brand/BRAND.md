# TraceableAI mark

The TraceableAI mark is the "reader bot". It is a sibling of the Coursebot head: the same robot head looks down into an open book. A green verified badge on the corner of the book stands for a citation that has been checked against its paper. On TraceableAI's own site, where the background is known, the bare mark is used without a tile, exactly as Coursebot does: the navy bot on light pages and the light bot on dark pages. Everywhere the background is not known (the favicon, app icons, slides, the README, other people's pages), the mark sits on its own rounded tile: the navy tile on light backgrounds and the light tile on dark ones. The word "TraceableAI" next to the tile follows the page's own text colour: navy on light pages and off-white on dark ones.

| File | Use it for |
|---|---|
| `traceableai-mark-navy.svg` | The site header in light mode (no tile). |
| `traceableai-mark-light.svg` | The site header in dark mode (no tile). Its face and pages are filled with the dark page colour `#0B1220`. |
| `traceableai-tile-navy.svg` | Light backgrounds you do not control: slides, the README, the paper, app icons. |
| `traceableai-tile-light.svg` | Dark pages and dark mode. |
| `favicon.svg` | The browser tab. It switches to the light tile when the browser is in dark mode. |
| `apple-touch-icon.png` | iOS home screen (navy tile, 180 px). |
| `*-512.png` | Places that cannot take SVG. |

## Colours

| Role | Light | Dark |
|---|---|---|
| Navy (tile, text, primary button) | `#13294B` | — |
| Page background | `#FFFFFF`, soft `#F6F8FB` | `#0B1220`, soft `#0E1626` |
| Text on dark | — | `#F2F5FA` |
| Antenna accent | `#F5B82E` | `#F5B82E` |
| Verified green (badge) | `#1E9E64` | `#27B072` |

Two details keep the mark readable at small sizes. The ring around the badge is always the colour of the tile, so the badge looks cut out of the book. The green is a filled shape, never a thin line, so it still shows at 16 px. Do not redraw the badge as an outline, and do not set the bare light mark on a page whose colour differs from `#0B1220`. Use the light tile there instead.

## Switching with the theme

When the site follows the operating system:

```html
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<picture>
  <source srcset="/traceableai-mark-light.svg" media="(prefers-color-scheme: dark)">
  <img src="/traceableai-mark-navy.svg" alt="TraceableAI" width="36" height="36">
</picture>
```

When the site has its own light/dark toggle, as Coursebot does, swap the `src` from the same class that switches the theme, instead of relying on the media query.

Design source: the Claude canvas "TraceableAI mascot ideation" (Logo page, artboards 16b to 16d; Site redesign page for the site).

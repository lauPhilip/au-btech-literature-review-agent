// Tailwind is compiled ahead of time (npm run build:css) into wwwroot/css/tailwind.css, which is committed.
// Only class names that appear literally in these files end up in the CSS, so never build a class name from
// a variable (write "bg-red-50", not "bg-" + colour + "-50").
//
// Design tokens: the colours, fonts and shadows every page shares. The reusable pieces built from them
// (buttons, cards, section headings, the verdict chips) are in Styles/tailwind.input.css as ui-* classes.
/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Components/**/*.{razor,cs,html,cshtml}',
    './Modules/**/*.{razor,cs,html,cshtml}',
    './wwwroot/js/**/*.js',
    './Styles/**/*.css',
  ],
  // Dark mode follows data-theme="dark" on <html>, which the theme toggle sets (not the system setting alone).
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    extend: {
      colors: {
        // The navy TraceableAI shares with Coursebot. A fixed colour, so existing pages look the same in either
        // theme until they move to the theme-aware tokens below.
        brand: {
          50: '#EEF2F8',
          100: '#D6DEEB',
          200: '#ADBDD6',
          500: '#2E4A7A',
          DEFAULT: '#13294B',
          700: '#0C1B33',
        },
        // Theme-aware tokens. Each is a CSS variable on :root and [data-theme="dark"] in Styles/tailwind.input.css,
        // written as "R G B" so opacity modifiers such as bg-page/80 work.
        page: 'rgb(var(--page) / <alpha-value>)',
        soft: 'rgb(var(--soft) / <alpha-value>)',
        panel: { DEFAULT: 'rgb(var(--panel) / <alpha-value>)', dot: 'rgb(var(--panel-dot) / <alpha-value>)' },
        ink: 'rgb(var(--text) / <alpha-value>)',
        heading: 'rgb(var(--navy) / <alpha-value>)',
        muted: 'rgb(var(--muted) / <alpha-value>)',
        line: { DEFAULT: 'rgb(var(--border) / <alpha-value>)', field: 'rgb(var(--field-border) / <alpha-value>)' },
        active: 'rgb(var(--active) / <alpha-value>)',
        primary: { DEFAULT: 'rgb(var(--primary) / <alpha-value>)', on: 'rgb(var(--on-primary) / <alpha-value>)' },
        accent: 'rgb(var(--accent) / <alpha-value>)',
        // The five citation verdicts: text, fill, border and the colour in charts and badges. Always shown
        // together with an icon or a word, never by colour alone.
        supported: {
          DEFAULT: 'rgb(var(--supported) / <alpha-value>)',
          fill: 'rgb(var(--supported-fill) / <alpha-value>)',
          border: 'rgb(var(--supported-border) / <alpha-value>)',
          chart: 'rgb(var(--supported-chart) / <alpha-value>)',
        },
        partly: {
          DEFAULT: 'rgb(var(--partly) / <alpha-value>)',
          fill: 'rgb(var(--partly-fill) / <alpha-value>)',
          border: 'rgb(var(--partly-border) / <alpha-value>)',
          chart: 'rgb(var(--partly-chart) / <alpha-value>)',
        },
        'not-supported': {
          DEFAULT: 'rgb(var(--not-supported) / <alpha-value>)',
          fill: 'rgb(var(--not-supported-fill) / <alpha-value>)',
          border: 'rgb(var(--not-supported-border) / <alpha-value>)',
          chart: 'rgb(var(--not-supported-chart) / <alpha-value>)',
        },
        'abstract-only': {
          DEFAULT: 'rgb(var(--abstract-only) / <alpha-value>)',
          fill: 'rgb(var(--abstract-only-fill) / <alpha-value>)',
          border: 'rgb(var(--abstract-only-border) / <alpha-value>)',
          chart: 'rgb(var(--abstract-only-chart) / <alpha-value>)',
        },
        unverifiable: {
          DEFAULT: 'rgb(var(--unverifiable) / <alpha-value>)',
          fill: 'rgb(var(--unverifiable-fill) / <alpha-value>)',
          border: 'rgb(var(--unverifiable-border) / <alpha-value>)',
          chart: 'rgb(var(--unverifiable-chart) / <alpha-value>)',
        },
        // The older verdict names, kept so existing pages keep working; they now use the same variables.
        verdict: {
          ok: 'rgb(var(--supported) / <alpha-value>)',
          okBg: 'rgb(var(--supported-fill) / <alpha-value>)',
          partial: 'rgb(var(--partly) / <alpha-value>)',
          partialBg: 'rgb(var(--partly-fill) / <alpha-value>)',
          bad: 'rgb(var(--not-supported) / <alpha-value>)',
          badBg: 'rgb(var(--not-supported-fill) / <alpha-value>)',
        },
        auBlue: '#13294B',
        auGrey: '#8A8D8F',
      },
      fontFamily: {
        // Self-hosted in wwwroot/fonts (Styles/tailwind.input.css), because the Content-Security-Policy allows
        // no third-party font hosts. Body text is Inter; headings, the wordmark and buttons are Poppins.
        sans: ['Inter', '"Segoe UI Variable"', '"Segoe UI"', 'system-ui', '-apple-system', 'BlinkMacSystemFont', 'Roboto', '"Helvetica Neue"', 'Arial', 'sans-serif'],
        display: ['Poppins', 'Inter', '"Segoe UI"', 'system-ui', 'sans-serif'],
        mono: ['ui-monospace', '"Cascadia Code"', '"SF Mono"', 'Menlo', 'Consolas', 'monospace'],
      },
      boxShadow: {
        card: '0 1px 2px rgba(16, 24, 40, 0.04), 0 1px 3px rgba(16, 24, 40, 0.06)',
        lift: '0 12px 32px -12px rgba(12, 27, 51, 0.25), 0 2px 6px rgba(16, 24, 40, 0.06)',
      },
      // Only buttons have rounded corners (rounded-btn); cards, panels, fields and labels are square.
      borderRadius: {
        btn: '0.375rem',
      },
      maxWidth: {
        prose: '68ch',
      },
    },
  },
  plugins: [],
};

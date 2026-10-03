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
    './wwwroot/js/**/*.js',
    './Styles/**/*.css',
  ],
  theme: {
    extend: {
      colors: {
        // Aarhus University navy, the one brand colour; everything else is grey so it stands out.
        brand: {
          50: '#EEF4F8',
          100: '#D7E5EE',
          200: '#AFCADC',
          500: '#1F5F86',
          DEFAULT: '#003B5C',
          700: '#00293F',
        },
        // The three citation verdicts. Always shown together with an icon or a word, never by colour alone.
        verdict: {
          ok: '#15803D',
          okBg: '#F0FDF4',
          partial: '#B45309',
          partialBg: '#FFFBEB',
          bad: '#B91C1C',
          badBg: '#FEF2F2',
        },
        auBlue: '#003B5C',
        auGrey: '#8A8D8F',
      },
      fontFamily: {
        // System fonts only: the Content-Security-Policy allows no third-party font hosts.
        sans: ['"Segoe UI Variable"', '"Segoe UI"', 'system-ui', '-apple-system', 'BlinkMacSystemFont', 'Inter', 'Roboto', '"Helvetica Neue"', 'Arial', 'sans-serif'],
        mono: ['ui-monospace', '"Cascadia Code"', '"SF Mono"', 'Menlo', 'Consolas', 'monospace'],
      },
      boxShadow: {
        card: '0 1px 2px rgba(16, 24, 40, 0.04), 0 1px 3px rgba(16, 24, 40, 0.06)',
        lift: '0 12px 32px -12px rgba(0, 41, 63, 0.25), 0 2px 6px rgba(16, 24, 40, 0.06)',
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

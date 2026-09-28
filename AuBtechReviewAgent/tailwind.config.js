// Tailwind is compiled ahead of time (npm run build:css) into wwwroot/css/tailwind.css, which is committed.
// Only class names that appear literally in these files end up in the CSS, so never build a class name from
// a variable (write "bg-red-50", not "bg-" + colour + "-50").
/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Components/**/*.{razor,cs,html,cshtml}',
    './wwwroot/js/**/*.js',
  ],
  theme: {
    extend: {
      colors: {
        auBlue: '#003B5C',
        auGrey: '#8A8D8F',
      },
    },
  },
  plugins: [],
};

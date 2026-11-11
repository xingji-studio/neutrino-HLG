const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const target = path.join(root, 'src/HLG.Host/wwwroot/vendor');
fs.mkdirSync(target, { recursive: true });
for (const [source, name] of [
  ['node_modules/@xterm/xterm/lib/xterm.js', 'xterm.js'],
  ['node_modules/@xterm/xterm/css/xterm.css', 'xterm.css'],
  ['node_modules/@xterm/addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['node_modules/@xterm/xterm/LICENSE', 'xterm-LICENSE'],
  ['node_modules/@xterm/addon-fit/LICENSE', 'addon-fit-LICENSE'],
]) fs.copyFileSync(path.join(root, source), path.join(target, name));

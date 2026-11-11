const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const target = path.join(root, 'src/HLG.Host/wwwroot/vendor');
const xterm = path.dirname(path.dirname(require.resolve('@xterm/xterm')));
const fit = path.dirname(path.dirname(require.resolve('@xterm/addon-fit')));
fs.mkdirSync(target, { recursive: true });
for (const [source, name] of [
  [path.join(xterm, 'lib/xterm.js'), 'xterm.js'],
  [path.join(xterm, 'css/xterm.css'), 'xterm.css'],
  [path.join(fit, 'lib/addon-fit.js'), 'addon-fit.js'],
  [path.join(xterm, 'LICENSE'), 'xterm-LICENSE'],
  [path.join(fit, 'LICENSE'), 'addon-fit-LICENSE'],
]) fs.copyFileSync(source, path.join(target, name));

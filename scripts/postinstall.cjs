require('./copy-terminal-assets.cjs');
if (process.platform === 'win32') {
  const { spawnSync } = require('node:child_process');
  // Installation remains usable if the SDK is installed later; launch retries the build.
  const result = spawnSync(process.execPath, [require.resolve('./neutrino-hlg.cjs'), '--prepare'], { windowsHide: true, encoding: 'utf8', env: process.env });
  if (result.status !== 0) {
    console.warn('Neutrino HLG 桌面程序准备失败，请检查 .NET 10 SDK 和构建日志；安装完成后可运行 neutrino-hlg 重试。');
    if (result.stderr) console.warn(result.stderr.trim());
    else if (result.error) console.warn(result.error.message);
  }
}

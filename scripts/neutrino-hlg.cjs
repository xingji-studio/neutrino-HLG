#!/usr/bin/env node
const path = require('node:path');
const { spawn } = require('node:child_process');

const args = process.argv.slice(2);
if (args.includes('--help') || args.includes('-h')) {
  console.log('用法：neutrino-hlg\n启动 Windows 主机。需要 .NET 10 SDK 和正在运行的 Neutrino 本机 API。');
} else if (args.includes('--version') || args.includes('-v')) {
  console.log(require('../package.json').version);
} else if (process.platform !== 'win32') {
  console.error('Neutrino HLG 主机目前仅支持 Windows。');
  process.exitCode = 1;
} else {
  const project = path.join(__dirname, '..', 'src', 'HLG.Host', 'HLG.Host.csproj');
  const child = spawn('dotnet', ['run', '--project', project, '--', ...args], { stdio: 'inherit', windowsHide: false });
  child.once('error', error => {
    console.error(error.code === 'ENOENT' ? '找不到 dotnet，请先安装 .NET 10 SDK。' : `无法启动 HLG：${error.message}`);
    process.exitCode = 1;
  });
  child.once('exit', (code, signal) => { process.exitCode = code ?? (signal ? 1 : 0); });
}

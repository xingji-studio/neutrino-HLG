#!/usr/bin/env node
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');

const args = process.argv.slice(2);
if (args.includes('--help') || args.includes('-h')) {
  console.log('用法：neutrino-hlg\n启动 Windows 桌面控制面板。需要 .NET 10 SDK 和 Neutrino 本机 API。');
} else if (args.includes('--version') || args.includes('-v')) {
  console.log(require('../package.json').version);
} else if (process.platform !== 'win32') {
  console.error('Neutrino HLG 主机目前仅支持 Windows。'); process.exitCode = 1;
} else {
  const prepareOnly = args.includes('--prepare');
  const root = path.resolve(__dirname, '..');
  const project = path.join(root, 'src', 'HLG.Host');
  const home = process.env.NEUTRINO_HOME || path.join(os.homedir(), '.neutrino');
  const logs = path.join(home, 'hlg', 'logs');
  const base = process.env.HLG_DESKTOP_CACHE || path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'Neutrino HLG', 'host');
  let logfile;
  function failure(message, detail) {
    try {
      fs.mkdirSync(logs, { recursive: true });
      logfile ||= path.join(logs, `${new Date().toISOString().replace(/[:.]/g, '-')}_${process.pid}_build.log`);
      fs.appendFileSync(logfile, `${message}\n${detail || ''}\n`);
    } catch {}
    if (prepareOnly) console.error(message + (logfile ? ` 日志：${logfile}` : ''));
    else {
      const text = message + (logfile ? `\n日志：${logfile}` : '');
      spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', 'Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show($env:HLG_STARTUP_MESSAGE, "Neutrino HLG", "OK", "Error") | Out-Null'], { windowsHide: true, stdio: 'ignore', env: { ...process.env, HLG_STARTUP_MESSAGE: text } });
    }
    process.exitCode = 1;
  }
  function fingerprint() {
    const hash = crypto.createHash('sha256');
    function visit(dir, relative = '') {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a,b)=>a.name.localeCompare(b.name))) {
        if (entry.name === 'bin' || entry.name === 'obj') continue;
        const rel = path.join(relative, entry.name), file = path.join(dir, entry.name);
        if (entry.isDirectory()) visit(file, rel);
        else if (entry.isFile()) { hash.update(rel); hash.update(fs.readFileSync(file)); }
      }
    }
    visit(project); return hash.digest('hex').slice(0, 20);
  }
  try {
    const output = path.join(base, fingerprint());
    const executable = path.join(output, 'HLG.Host.exe');
    const marker = path.join(output, '.complete');
    if (!fs.existsSync(executable) || !fs.existsSync(marker)) {
      fs.mkdirSync(output, { recursive: true }); fs.mkdirSync(logs, { recursive: true });
      logfile = path.join(logs, `${new Date().toISOString().replace(/[:.]/g, '-')}_${process.pid}_build.log`);
      const intermediate = path.join(output, 'obj') + path.sep;
      const result = spawnSync('dotnet', ['build', path.join(project, 'HLG.Host.csproj'), '--configuration', 'Release', '--output', output, `-p:BaseIntermediateOutputPath=${intermediate}`, `-p:MSBuildProjectExtensionsPath=${intermediate}`, '--nologo'], { windowsHide: true, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
      fs.writeFileSync(logfile, (result.stdout || '') + (result.stderr || '') + (result.error ? String(result.error) : ''));
      if (result.status !== 0 || !fs.existsSync(executable)) throw new Error(result.error?.code === 'ENOENT' ? '找不到 dotnet，请先安装 .NET 10 SDK。' : '无法构建 Neutrino HLG，请检查 .NET 10 SDK。');
      fs.writeFileSync(marker, 'ok\n');
    }
    const shortcutScript = "$p=$env:HLG_SHORTCUT_DIR; if (-not $p) { $p=[Environment]::GetFolderPath('Programs') }; New-Item -ItemType Directory -Force -Path $p | Out-Null; $s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $p 'Neutrino HLG.lnk')); $s.TargetPath=$env:HLG_SHORTCUT_TARGET; $s.WorkingDirectory=[IO.Path]::GetDirectoryName($s.TargetPath); $s.Save()";
    const shortcut = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', shortcutScript], { windowsHide: true, encoding: 'utf8', env: { ...process.env, HLG_SHORTCUT_TARGET: executable } });
    if (shortcut.status !== 0) { try { fs.mkdirSync(logs, { recursive: true }); logfile ||= path.join(logs, `${new Date().toISOString().replace(/[:.]/g, '-')}_${process.pid}_build.log`); fs.appendFileSync(logfile, `创建开始菜单快捷方式失败：${shortcut.stderr || shortcut.error || shortcut.status}\n`); } catch {} }
    if (!prepareOnly) {
      const child = spawn(executable, args, { detached: true, stdio: 'ignore', windowsHide: false });
      child.once('error', error => failure('无法启动 Neutrino HLG。', error.message)); child.unref();
    }
  } catch (error) { failure(error.message || '无法启动 Neutrino HLG。', error.stack); }
}

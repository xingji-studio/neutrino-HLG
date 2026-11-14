const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

if (process.platform !== 'win32') throw new Error('This integration test requires Windows.');
const root = path.resolve(__dirname, '..');
const tempRoot = path.join(root, 'tests', 'HLG.Desktop', 'bin');
fs.mkdirSync(tempRoot, { recursive: true });
const temp = fs.mkdtempSync(path.join(tempRoot, 'hlg-launcher-'));
try {
  const fixture = path.join(temp, 'project');
  fs.mkdirSync(path.join(fixture, 'src'), { recursive: true });
  fs.cpSync(path.join(root, 'src', 'HLG.Host'), path.join(fixture, 'src', 'HLG.Host'), {
    recursive: true,
    filter: source => !['bin', 'obj'].includes(path.basename(source))
  });
  fs.cpSync(path.join(root, 'scripts'), path.join(fixture, 'scripts'), { recursive: true });
  // A previously built checkout contains generated assembly attributes under obj.
  // These must remain excluded when the launcher puts its intermediate files elsewhere.
  const oldObj = path.join(fixture, 'src', 'HLG.Host', 'obj', 'Debug', 'net10.0-windows');
  fs.mkdirSync(oldObj, { recursive: true });
  fs.writeFileSync(path.join(oldObj, 'Old.AssemblyInfo.cs'), '[assembly: System.Reflection.AssemblyCompany("stale")]\n');
  const env = { ...process.env, HLG_DESKTOP_CACHE: path.join(temp, 'cache'), HLG_SHORTCUT_DIR: path.join(temp, 'shortcuts'), NEUTRINO_HOME: path.join(temp, 'neutrino') };
  const launch = path.join(fixture, 'scripts', 'neutrino-hlg.cjs');
  const build = spawnSync(process.execPath, [launch, '--prepare'], { env, encoding: 'utf8', timeout: 120000 });
  if (build.status !== 0) {
    const logDir = path.join(env.NEUTRINO_HOME, 'hlg', 'logs');
    const logs = fs.existsSync(logDir) ? fs.readdirSync(logDir).map(name => fs.readFileSync(path.join(logDir, name), 'utf8')).join('\n') : '';
    throw new Error(`desktop build failed: ${build.stderr}\n${logs}`);
  }
  const output = path.join(env.HLG_DESKTOP_CACHE, fs.readdirSync(env.HLG_DESKTOP_CACHE)[0]);
  assert(fs.existsSync(path.join(output, 'HLG.Host.exe')));
  assert(fs.existsSync(path.join(output, '.complete')));
  assert(fs.existsSync(path.join(env.HLG_SHORTCUT_DIR, 'Neutrino HLG.lnk')));
  const executable = fs.readFileSync(path.join(output, 'HLG.Host.exe'));
  assert.equal(executable.readUInt16LE(executable.readUInt32LE(0x3c) + 24 + 68), 2, 'Windows GUI subsystem required');
  const blocked = path.join(temp, 'not-a-directory');
  fs.writeFileSync(blocked, 'test');
  const failedInstall = spawnSync(process.execPath, [path.join(fixture, 'scripts', 'postinstall.cjs')], {
    env: { ...env, HLG_DESKTOP_CACHE: blocked }, encoding: 'utf8', timeout: 30000
  });
  assert.equal(failedInstall.status, 0, 'npm installation should remain usable');
  assert.match(failedInstall.stderr, /桌面程序准备失败/, 'failed preparation must be visible');
  console.log('Desktop launcher builds with existing obj files and creates a GUI shortcut.');
} finally { fs.rmSync(temp, { recursive: true, force: true }); }

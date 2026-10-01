// 阿助桌宠 <-> Trae 令牌同步扩展。
//
// 做什么：激活时向 Trae 要一份**当前会话的云令牌**（就是 Trae 自己拼 `authorization`
//   头用的那条内部命令 `icube.cloudide.getByteCloudToken`），然后只改阿助凭据文件里的
//   `authorization:` 那一行。14 天到期再也不用手工粘贴。
//
// 不做 什么：不抓包、不读进程内存、不逆向、不联网、不伪造签名头。
//   只动 `authorization` 一行：其它行原样、行尾原样、不重排、不增删。
//
// 诊断追加写到 <globalStorage>/azhupet_trae_sync.log 与 os.tmpdir()/azhupet_trae_sync.log。
// **令牌本身从不落日志**（只记长度与 iat/exp）。
//
// ⚠ 这个文件是**随桌宠分发**的（桌宠「余额配置 → 安装同步扩展」把它拷进
//   ~/.trae-cn/extensions/）。真身在 pet/trae-ext/（仓库）。
//
// ⚠⚠ 投放口径（2026-10-01 六次现场实验定案）：Trae SOLO CN 是 `solo-lite` 构建，
//   **启动时根本不扫用户扩展目录**，唯一入口是它的目录 watcher 对「**新增目录**」的反应；
//   而且**同一个 id 在一个 Trae 会话里只会激活一次**。⇒ 桌宠每次投放都用一个新的目录名
//   （azhupet.trae-token-sync-<版本>-<UTC 时间戳>），所以扩展目录里会积累多份副本 —— 那是
//   **设计使然**，不是垃圾（删掉换不回激活，只会往 Trae 的 .obsolete 里写字）。
//   ⇒ 推论：**Trae 重启后本扩展不会自己回来**，要再点一次「安装同步扩展」。

const fs = require('fs');
const os = require('os');
const path = require('path');

const CMD = 'icube.cloudide.getByteCloudToken';
const CRED_FILE = 'balance_secret.txt';
const TMP_LOG = path.join(os.tmpdir(), 'azhupet_trae_sync.log');

let vscode = null;
let logPath = TMP_LOG;

function log(s) {
  const line = '[' + new Date().toISOString() + '] ' + s + '\n';
  const targets = [logPath, TMP_LOG];
  for (let i = 0; i < targets.length; i++) {
    try {
      fs.mkdirSync(path.dirname(targets[i]), { recursive: true });
      fs.appendFileSync(targets[i], line, 'utf8');
    } catch (e) { /* ignore */ }
  }
}

// ⚠⚠ 这个落点必须与桌宠侧 StatusProbe.SecretDir() **逐字一致**（同一份数据只许一个口径）：
//   优先环境变量 AZHU_SECRET_DIR，否则 %LOCALAPPDATA%\AzhuPet。
//   --traeexttest 会同时问 C# 与 node「你算出来的路径是什么」，两边不等就报红 ——
//   因为这类不一致的症状是「扩展明明在写、桌宠却一直说没配置」，中间一个错都不报。
function secretDir() {
  const env = process.env.AZHU_SECRET_DIR;
  if (env && env.length) return env;
  const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(base, 'AzhuPet');
}

function credPath() {
  return path.join(secretDir(), CRED_FILE);
}

function decodeTimes(tok) {
  try {
    const p = tok.split('.')[1];
    const j = JSON.parse(Buffer.from(p.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
    return {
      iat: j.iat ? new Date(j.iat * 1000).toISOString() : null,
      exp: j.exp ? new Date(j.exp * 1000).toISOString() : null
    };
  } catch (e) { return null; }
}

function isMarker(line) { return /^\s*---/.test(line); }
function isHeadersMarker(line) { return /^---h/i.test(line.trim()); }
// ⚠ 只认「行首就是 scheme://」的形态。不能用「name: value 不像」来排除 ——
//   `https://api.trae.cn/...` 自己就长得像 `https: //...`，会被当成请求头（2026-09-30 冒烟测到）。
function isUrlLine(line) { return /^[a-z][a-z0-9+.-]*:\/\//i.test(line.trim()); }

// 把令牌写进**一份已有的**凭据文本（纯函数，可以被 --traeexttest 离线喂合成文本）。
// 三种情形，除此之外一律不动：
//   A 已有 `authorization:` 行      → 只换值（行数不变、其它行逐字不变）
//   B 没有该行、但有 `---headers---` → 紧随其后插入一行（行数 +1）
//   C 连 `---headers---` 都没有     → 插在 URL 行之后；连 URL 都没有 ⇒ 拒绝（不猜）
// ⚠ 为什么不重写整个文件：那份文件里还有 28 个**设备级**的头（x-device-id、
//   x-market-user-id …），它们是「这台机器这个账号」的事实，扩展无从生成。
//   只改一行 = 最多只有一处能错。
function patchText(raw, tok) {
  const eol = raw.indexOf('\r\n') >= 0 ? '\r\n' : '\n';
  const lines = raw.split(eol);
  const re = /^(\s*authorization\s*:\s*Cloud-IDE-JWT\s+)(\S+)(\s*)$/i;

  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(re);
    if (!m) continue;
    const newVal = tok.trim();
    if (m[2] === newVal)
      return { ok: true, changed: false, how: 'replace-same', text: raw, eol: eol, oldLen: m[2].length, newLen: newVal.length };
    const out = lines.slice();
    out[i] = m[1] + newVal;
    if (out.length !== lines.length) return { ok: false, why: 'linecount', eol: eol };
    return { ok: true, changed: true, how: 'replace',
             text: out.join(eol), eol: eol, oldLen: m[2].length, newLen: newVal.length };
  }

  const newLine = 'authorization: Cloud-IDE-JWT ' + tok.trim();
  let anchor = lines.findIndex(isHeadersMarker);
  let how = 'insert-after-headers';
  if (anchor < 0) {
    anchor = lines.findIndex(isUrlLine);
    how = 'insert-after-url';
  }
  if (anchor < 0) return { ok: false, why: 'no-anchor', eol: eol };

  const out = lines.slice(0, anchor + 1).concat([newLine], lines.slice(anchor + 1));
  return { ok: true, changed: true, how: how, text: out.join(eol), eol: eol,
           oldLen: 0, newLen: tok.trim().length };
}

// 落盘。⚠ 文件**不存在时不创建** —— 骨架里有 URL 与 body，那是桌宠的事实（它才负责
//   请求长什么样），扩展凭空造一份等于给同一份数据加第二个口径。
function patchCredential(tok) {
  const f = credPath();
  if (!fs.existsSync(f)) {
    log('cred file MISSING: ' + f + ' （先在阿助「余额配置」里点『安装同步扩展』）');
    return { ok: false, why: 'missing', path: f };
  }

  const raw = fs.readFileSync(f, 'utf8');
  const r = patchText(raw, tok);
  if (!r.ok) { log('patch REFUSED: ' + r.why + ' in ' + f); return { ok: false, why: r.why, path: f }; }
  if (!r.changed) { log('credential already up to date (len=' + r.newLen + ')'); return { ok: true, changed: false, how: r.how }; }

  try { fs.writeFileSync(f + '.bak-autosync', raw, 'utf8'); }
  catch (e) { log('backup failed: ' + (e && e.message ? e.message : String(e))); }
  fs.writeFileSync(f, r.text, 'utf8');
  log('credential ' + (r.how === 'replace' ? 'UPDATED' : 'INSERTED') + ': ' + f
      + ' | oldLen=' + r.oldLen + ' newLen=' + r.newLen
      + ' bytes=' + Buffer.byteLength(r.text, 'utf8'));
  return { ok: true, changed: true, how: r.how, oldLen: r.oldLen, newLen: r.newLen };
}

async function sync() {
  log('--- sync start ---');
  try {
    const tok = await vscode.commands.executeCommand(CMD);
    if (typeof tok !== 'string' || tok.length === 0) {
      log('unexpected result type=' + typeof tok + ' preview=' + JSON.stringify(tok).slice(0, 200));
      return;
    }
    const t = decodeTimes(tok);
    log('token ok len=' + tok.length + ' iat=' + (t && t.iat) + ' exp=' + (t && t.exp));
    log('patch result = ' + JSON.stringify(patchCredential(tok)));
  } catch (e) {
    log('SYNC FAILED: ' + (e && e.message ? e.message : String(e)));
  }
}

function activate(context) {
  try {
    vscode = require('vscode');
    try { logPath = path.join(context.globalStorageUri.fsPath, 'azhupet_trae_sync.log'); } catch (e) { /* keep tmp */ }
    log('=== activate ===');
    try { log('app=' + vscode.env.appName + ' v=' + vscode.version + ' extPath=' + context.extensionPath); } catch (e) { }

    // ⚠ registerCommand 遇重名会 **throw**，而它排在 sync() 前面 —— 不包 try 的话
    //   整个 activate 会被打断，副本扩展因此一行业务逻辑都跑不到（2026-09-30 踩过）。
    try {
      context.subscriptions.push(
        vscode.commands.registerCommand('azhupet.syncTraeToken', function () { sync(); return 'sync started'; })
      );
    } catch (e) {
      log('registerCommand failed (duplicate?): ' + (e && e.message ? e.message : String(e)));
    }

    sync();
  } catch (e) {
    log('ACTIVATE THREW: ' + (e && e.message ? e.message : String(e)));
  }
}

function deactivate() { }

module.exports = {
  activate: activate,
  deactivate: deactivate,
  // 以下为**判据入口**（--traeexttest 会 require 本文件直接调它们）：
  secretDir: secretDir,
  credPath: credPath,
  patchText: patchText,
  patchCredential: patchCredential,
  CMD: CMD
};

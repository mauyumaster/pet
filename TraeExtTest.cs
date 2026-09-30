// --traeexttest：离线验「随包分发的 Trae 同步扩展」这条链路。
//
// 为什么需要它：这条链路跨了 **C# / JS 两种语言、两个进程、三个文件**（桌宠 → 扩展目录 → 凭据文件），
// 而每一段的失败长得都一样 —— 「气泡里 Trae 那行写『取不到』」。
//   · 扩展没被投放到对的地方 ⇒ 看起来像接口坏了
//   · engines.vscode 写成 "*"  ⇒ 扩展被登记但**永不激活**，零日志（2026-09-30 栽过一次）
//   · JS 算的凭据路径与 C# 不一致 ⇒ 扩展在写、桌宠说没配置
//   · 骨架格式与自家解析器对不上 ⇒ 文件明明写出来了，桌宠说「凭据无 URL」
// 这些都是「纯读代码看不出来」的，所以每一条都要有能变红的断言。
//
// 判据分两层：
//   ① **纯 C# 层**（永远跑）：源文件齐备 / package.json 合法 / 骨架能被自家解析器读懂 / 候选挑选逻辑
//   ② **真跑 JS 层**（有 node 才跑）：让 node 去 require **真身 extension.js**，
//      在临时目录里造合成凭据，验「只动一行 / 行尾不变 / 无行时插入 / 无文件时不创建」。
//      ⚠ 没有 node 时打印 [SKIP] —— **不装作通过**（本项目纪律：假绿比失败更糟）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class TraeExtTest
    {
        public static int Run(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.WriteLine("---- Trae 同步扩展（离线判据）----");

            int pass = 0, fail = 0, skip = 0;

            // ==================== ① 纯 C# 层 ====================

            var st = TraeExtInstaller.Inspect();
            Console.WriteLine("源目录   : " + st.SourceDir);
            Console.WriteLine("Trae 扩展目录: " + (st.HasTrae ? st.ExtDir : "(没找到)"));
            Console.WriteLine("当前状态 : " + st.State + " · " + st.Detail);

            string why;
            bool ready = TraeExtInstaller.SourceReady(out why);
            Report(ready, "源文件齐备（" + TraeExtInstaller.JsFile + " + " + TraeExtInstaller.ManifestFile + "）",
                   why ?? "", ref pass, ref fail);

            // 两个文件都要真的读得到内容（防「目录里有个 0 字节同名文件」）
            try
            {
                string p = Path.Combine(st.SourceDir, TraeExtInstaller.ManifestFile);
                var doc = JsonDocument.Parse(File.ReadAllText(p));
                var root = doc.RootElement;
                string name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                string pub = root.TryGetProperty("publisher", out var pb) ? pb.GetString() : null;
                string eng = null;
                if (root.TryGetProperty("engines", out var en) && en.TryGetProperty("vscode", out var vv)) eng = vv.GetString();

                Report(pub + "." + name == TraeExtInstaller.PubId, "package.json 的 publisher.name 与 PubId 一致",
                       "pub=" + pub + " name=" + name + " 期望 " + TraeExtInstaller.PubId, ref pass, ref fail);
                Report(root.TryGetProperty("version", out var ver) && ver.GetString() == TraeExtInstaller.Version,
                       "package.json 的 version 与 TraeExtInstaller.Version 一致",
                       "manifest=" + (root.TryGetProperty("version", out var v2) ? v2.GetString() : "?") + " C#=" + TraeExtInstaller.Version,
                       ref pass, ref fail);
                // ⚠⚠ 这条就是 2026-09-30 那个坑：写成 "*" 会被 Trae 拒绝（"版本不够具体"），
                //   扩展进 profile 但永不激活，且**没有任何日志**。内置扩展写 "*" 是被豁免的，别照抄。
                Report(!string.IsNullOrEmpty(eng) && eng != "*" && eng != "",
                       "engines.vscode 是具体范围（不能是 *）", "engines.vscode=" + (eng ?? "(缺)"), ref pass, ref fail);
                Report(root.TryGetProperty("main", out var mn) && mn.GetString() == "./" + TraeExtInstaller.JsFile,
                       "package.json 的 main 指向 " + TraeExtInstaller.JsFile,
                       "main=" + (root.TryGetProperty("main", out var m2) ? m2.GetString() : "?"), ref pass, ref fail);
            }
            catch (Exception ex)
            {
                Report(false, "package.json 可解析", ex.Message, ref pass, ref fail);
            }

            // 骨架（不落盘，纯文本）必须能被**自家的解析器**读懂 —— 否则症状是
            // 「文件明明写出来了，桌宠说凭据无 URL」，两边代码各自都自洽。
            {
                string url, method, body;
                List<KeyValuePair<string, string>> headers;
                StatusProbe.BuildTraeRequest(TraeExtInstaller.SkeletonText(), out url, out method, out body, out headers);
                Report(url == TraeExtInstaller.EntitlementUrl, "骨架的第 1 行能被自家解析器读成 URL",
                       "读到 " + (url ?? "(null)"), ref pass, ref fail);
                Report(method == "POST", "骨架缺 method 段时回落 POST", "method=" + method, ref pass, ref fail);
                Report(headers.Count == 0 && string.IsNullOrEmpty(body), "骨架自身没有多余的头/体",
                       "headers=" + headers.Count + " body=" + (body ?? "(null)"), ref pass, ref fail);

                // 扩展插入一行之后的形态，也必须读得懂（这是真实生效时的文件长相）
                string afterInsert = TraeExtInstaller.EntitlementUrl
                    + "\n---headers---\nauthorization: Cloud-IDE-JWT DUMMY.TOKEN.for.test\n---body---";
                StatusProbe.BuildTraeRequest(afterInsert, out url, out method, out body, out headers);
                Report(url == TraeExtInstaller.EntitlementUrl && headers.Count == 1
                       && string.Equals(headers[0].Key, "authorization", StringComparison.OrdinalIgnoreCase),
                       "插入 authorization 一行后仍解析正确（URL + 恰好 1 个头）",
                       "url=" + url + " headers=" + headers.Count, ref pass, ref fail);
            }

            // 候选挑选（纯函数）：第一个存在的胜出；都不存在返回 null
            {
                string home = Path.Combine(Path.GetTempPath(), "azhu-home-probe");
                var cands = TraeExtInstaller.DataDirCandidates(home, null);
                Report(cands.Contains(Path.Combine(home, ".trae-cn")), "候选清单包含 <home>\\.trae-cn",
                       "候选=" + string.Join(" | ", cands.ToArray()), ref pass, ref fail);

                string ex1 = Path.Combine(home, ".trae-cn");
                string got = TraeExtInstaller.PickExtensionsDir(cands, p => p == ex1);
                Report(got == Path.Combine(ex1, "extensions"), "PickExtensionsDir 取第一个存在的候选",
                       "得到 " + got, ref pass, ref fail);
                Report(TraeExtInstaller.PickExtensionsDir(cands, p => false) == null,
                       "PickExtensionsDir 都不存在时返回 null", "", ref pass, ref fail);
            }

            // 本机落点（只报，不判红 —— 换台机器可能真没装 Trae）
            if (st.HasTrae)
                Report(st.ExtDir.EndsWith("extensions", StringComparison.OrdinalIgnoreCase),
                       "解析出的落点以 extensions 结尾", st.ExtDir, ref pass, ref fail);
            else
            { Console.WriteLine("[SKIP] 解析落点：本机没找到 Trae 数据目录（不是通过）"); skip++; }

            // ==================== ② 真跑 JS 层 ====================

            string node = FindNode();
            if (node == null)
            {
                Console.WriteLine("[SKIP] 真跑 extension.js：没找到 node.exe（**未验证，不是通过**）"); skip++;
            }
            else
            {
                Console.WriteLine("node = " + node);
                string work = Path.Combine(Path.GetTempPath(), "azhupet_traeext_" + DateTime.Now.ToString("HHmmss"));
                string harness = Path.Combine(work, "harness.js");
                try
                {
                    Directory.CreateDirectory(work);
                    File.WriteAllText(harness, Harness, new UTF8Encoding(false));
                    string expectedCred = Path.Combine(StatusProbe.SecretDir(), StatusProbe.TraeSecretFile);
                    var psi = new ProcessStartInfo(node)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    psi.ArgumentList.Add(harness);
                    psi.ArgumentList.Add(Path.Combine(st.SourceDir, TraeExtInstaller.JsFile));
                    psi.ArgumentList.Add(work);
                    psi.ArgumentList.Add(expectedCred);

                    // ⚠⚠ 把子进程的 TMP/TEMP 指到临时工作目录：扩展的诊断日志走 os.tmpdir()，
                    //   不改就会**把合成用例的日志混进 Trae 里那份生产诊断日志**
                    //   （2026-09-30 实测：跑完判据后真日志里多出 8 行指向 temp\azhupet_traeext_*\caseN）。
                    //   测试污染生产日志的后果是「排障时看到的现场是测试造的」—— 比没有日志更坏。
                    psi.EnvironmentVariables["TMP"] = work;
                    psi.EnvironmentVariables["TEMP"] = work;

                    string stdout, stderr;
                    int rc;
                    using (var p = Process.Start(psi))
                    {
                        stdout = p.StandardOutput.ReadToEnd();
                        stderr = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } rc = -1; }
                        else rc = p.ExitCode;
                    }
                    foreach (var line in stdout.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        if (t.StartsWith("PASS ", StringComparison.Ordinal)) { pass++; Console.WriteLine("  [PASS] " + t.Substring(5)); }
                        else if (t.StartsWith("FAIL ", StringComparison.Ordinal)) { fail++; Console.WriteLine("  [FAIL] " + t.Substring(5)); }
                        else Console.WriteLine("  " + t);
                    }
                    if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine("  (node stderr) " + stderr.Trim());
                    if (rc != 0)
                    { Console.WriteLine("[FAIL] node 退出码 = " + rc + "（期望 0）"); fail++; }
                }
                catch (Exception ex)
                { Console.WriteLine("[FAIL] 跑 node 失败：" + ex.Message); fail++; }
                finally
                {
                    // ⚠ 临时目录里是**合成的假令牌**，不是真凭据；仍然照删不误 —— 免得日后误认。
                    try { Directory.Delete(work, true); } catch { }
                }
            }

            Console.WriteLine("Trae 扩展判据：" + pass + " 通过 / " + fail + " 失败" + (skip > 0 ? " / " + skip + " 跳过（跳过≠通过）" : ""));
            return fail == 0 ? 0 : 1;
        }

        private static void Report(bool ok, string name, string detail, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail)); }
        }

        /// <summary>找 node.exe。**找不到不算失败**（目标机未必有 node），但绝不能算通过。</summary>
        private static string FindNode()
        {
            var cands = new List<string>();
            string env = Environment.GetEnvironmentVariable("NODE_EXE");
            if (!string.IsNullOrEmpty(env)) cands.Add(env);
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                cands.Add(Path.Combine(pf, "nodejs", "node.exe"));
                cands.Add(Path.Combine(lad, "Programs", "nodejs", "node.exe"));
            }
            catch { }
            foreach (var c in cands) if (File.Exists(c)) return c;
            try
            {
                var psi = new ProcessStartInfo("where.exe", "node")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return null; }
                    if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim())) return line.Trim();
                }
            }
            catch { }
            return null;
        }

        // 交给 node 跑的判据。⚠ 全用单引号，免得 C# verbatim 字符串里到处转义。
        // 参数：argv[2]=extension.js 路径，argv[3]=工作目录，argv[4]=C# 算出的凭据路径。
        private const string Harness = @"
const fs = require('fs');
const path = require('path');

const extPath = process.argv[2];
const work = process.argv[3];
const expectCred = process.argv[4];

let fails = 0;
function chk(name, cond, detail) {
  if (cond) console.log('PASS ' + name);
  else { fails++; console.log('FAIL ' + name + (detail ? ' :: ' + detail : '')); }
}

const m = require(extPath);
const U = 'https://api.trae.cn/trae/api/v2/pay/user_current_entitlement_list';
const TOK = 'DUMMY.TOKEN.for.test';

chk('模块导出 patchText / patchCredential / credPath',
    typeof m.patchText === 'function' && typeof m.patchCredential === 'function' && typeof m.credPath === 'function');

// ⚠ 比的是「同一个文件」，不是「字面拼法」：Windows 上 / 与 \\ 等价、大小写也等价。
//   2026-09-30 第一版直接比字符串，结果被 Git Bash 把参数里的反斜杠转成正斜杠，
//   判出一条**不存在的**红 —— 判据自己造的红噪音会让人不再相信它。
//   （仍把两边的原样值打进 detail，万一真的不是同一个文件，看得见差异在哪。）
function norm(p) { return String(p).replace(/\\/g, '/').toLowerCase(); }
chk('凭据路径口径与 C# 一致', norm(m.credPath()) === norm(expectCred),
    'js=' + m.credPath() + ' cs=' + expectCred);

const A = [U, '---headers---', 'Host: api.trae.cn', 'authorization: Cloud-IDE-JWT OLDTOKEN', 'x-device-id: abc', '---', '---body---'].join('\n');
const r1 = m.patchText(A, TOK);
const a1 = r1.text.split('\n');
chk('替换：只换 authorization 那一行的值',
    r1.ok && r1.changed && a1[3] === 'authorization: Cloud-IDE-JWT ' + TOK, a1[3]);
chk('替换：行数不变', a1.length === A.split('\n').length, a1.length + ' vs ' + A.split('\n').length);
chk('替换：其余行逐字不变',
    a1.filter((x, i) => i !== 3).join('|') === A.split('\n').filter((x, i) => i !== 3).join('|'));

const SAME = [U, '---headers---', 'authorization: Cloud-IDE-JWT ' + TOK].join('\n');
const r2 = m.patchText(SAME, TOK);
chk('同值：判为无需改动且文本原样返回', r2.ok && r2.changed === false && r2.text === SAME);

const B = [U, '---headers---', 'Host: api.trae.cn', '---body---'].join('\n');
const r3 = m.patchText(B, TOK);
const b1 = r3.text.split('\n');
chk('插入：没有 authorization 行时插在 ---headers--- 之后',
    r3.ok && r3.changed && b1[2] === 'authorization: Cloud-IDE-JWT ' + TOK, b1[2]);
chk('插入：行数 +1', b1.length === B.split('\n').length + 1, b1.length + ' vs ' + (B.split('\n').length + 1));

const C = [U, 'x-device-id: abc'].join('\n');
const r4 = m.patchText(C, TOK);
chk('插入：没有 ---headers--- 时锚在 URL 行之后',
    r4.ok && r4.text.split('\n')[1] === 'authorization: Cloud-IDE-JWT ' + TOK);

const r5 = m.patchText('not-a-credential-at-all', TOK);
chk('拒绝：既无 URL 也无段落标记时不猜', r5.ok === false && r5.why === 'no-anchor', r5.why);

const D = [U, '---headers---', 'authorization: Cloud-IDE-JWT OLDTOKEN'].join('\r\n');
const r6 = m.patchText(D, TOK);
chk('CRLF 文件：行尾不被改成 LF',
    r6.ok && r6.text.indexOf('\r\n') >= 0 && r6.text.indexOf('\n') === r6.text.indexOf('\r\n') + 1,
    JSON.stringify(r6.text.slice(0, 60)));

function mkdir(n) { const d = path.join(work, n); fs.mkdirSync(d, { recursive: true }); return d; }

const d1 = mkdir('case1');
process.env.AZHU_SECRET_DIR = d1;
const f1 = path.join(d1, 'balance_secret.txt');
fs.writeFileSync(f1, A, 'utf8');
const w1 = m.patchCredential(TOK);
const after1 = fs.readFileSync(f1, 'utf8');
chk('落盘：替换成功', w1 && w1.ok === true && w1.changed === true, JSON.stringify(w1));
chk('落盘：行数与其它行都不变',
    after1.split('\n').length === A.split('\n').length &&
    after1.split('\n').filter((x, i) => i !== 3).join('|') === A.split('\n').filter((x, i) => i !== 3).join('|'));
chk('落盘：留下 .bak-autosync 且内容 = 改前的原件',
    fs.existsSync(f1 + '.bak-autosync') && fs.readFileSync(f1 + '.bak-autosync', 'utf8') === A);
chk('落盘：目录里只多出这两个文件',
    fs.readdirSync(d1).sort().join(',') === 'balance_secret.txt,balance_secret.txt.bak-autosync',
    fs.readdirSync(d1).join(','));

const d2 = mkdir('case2');
process.env.AZHU_SECRET_DIR = d2;
const w2 = m.patchCredential(TOK);
chk('无文件：拒绝（不越权凭空创建凭据）',
    w2 && w2.ok === false && w2.why === 'missing' && !fs.existsSync(path.join(d2, 'balance_secret.txt')), JSON.stringify(w2));

const d3 = mkdir('case3');
process.env.AZHU_SECRET_DIR = d3;
const f3 = path.join(d3, 'balance_secret.txt');
fs.writeFileSync(f3, B, 'utf8');
const w3 = m.patchCredential(TOK);
const after3 = fs.readFileSync(f3, 'utf8');
chk('落盘：无 authorization 行时插入成功',
    w3 && w3.ok === true && w3.how === 'insert-after-headers', JSON.stringify(w3));
chk('落盘：插入后 ---headers--- 紧随的就是 authorization 行',
    after3.split('\n')[2] === 'authorization: Cloud-IDE-JWT ' + TOK, after3.split('\n')[2]);

chk('诊断日志落在临时工作目录（没写进 Trae 里那份生产日志）',
    fs.existsSync(path.join(work, 'azhupet_trae_sync.log')),
    '找不到 ' + path.join(work, 'azhupet_trae_sync.log') + ' ⇒ TMP/TEMP 没被子进程继承');

console.log('HARNESS_FAILS=' + fails);
process.exit(fails === 0 ? 0 : 1);
";
    }
}

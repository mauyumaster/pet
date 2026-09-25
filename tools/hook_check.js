// tools/hook_check.js —— 把「注入页面的那段钩子 JS」真跑一遍（Node，不联网、不开浏览器）。
//
// 用法（脚本从 stdin 来，来源就是程序自己那一份，不是这里手抄的副本）：
//     pet.exe --hookscript | node tools/hook_check.js
//
// ⚠ 为什么非要有它：钩子脚本有语法错时，AddScriptToExecuteOnDocumentCreatedAsync
//   **不会报错** —— 那一步只是「把脚本注册到这个 WebView2 上」，真正执行是在每个新文档
//   创建时。于是脚本坏了的表现是「钩子压根没装」，与「页面没发那个请求」长得一模一样：
//   用户看到同一句提示，我们也无从分辨。这条路径偏偏只能靠「登一次看看」来发现。
//   （对照：balanceconfigtest 只能断言「脚本里含某个字符串」—— 那不叫执行过。）
//
// ⚠⚠ 证据边界：这是个**最小**浏览器环境，不是真 Chromium。它能验的是「脚本自身的逻辑」
//   （包裹 fetch/XHR、挑出目标请求、记录诊断列表），验不了浏览器强制的规范行为 ——
//   最典型的是 **cookie 头**：真实浏览器禁止脚本设置/读取它，这里的桩不模拟那条限制，
//   所以「cookie 拿不到」在本文件里**测不出来**，那是宿主侧 CookieManager 的职责。
const fs = require('node:fs');

let pass = 0, fail = 0;
function check(ok, name) {
    if (ok) { pass++; console.log('[PASS] ' + name); } else { fail++; console.log('[FAIL] ' + name); }
}

function main() {
    const script = fs.readFileSync(0, 'utf8').trim();
    if (!script) {
        console.log('[FAIL] stdin 是空的 —— 用法：pet.exe --hookscript | node tools/hook_check.js');
        return 1;
    }

    // 语法关：先证明这段脚本本身能被解析，否则后面所有行为断言都没有意义。
    try {
        new Function(script);
        check(true, '脚本语法可解析（new Function 不抛）');
    } catch (e) {
        check(false, '脚本语法可解析 —— ' + e.message);
        return 1;
    }

    // 造一个**干净**的最小环境。每个用例一份、互不干扰 ——
    // 脚本靠 window.__azhuHook 做「只装一次」的守卫，共用一个 window 会让后面的用例装不上。
    function makeEnv(href) {
        const w = {};
        const loc = { href: href || 'https://www.workbuddy.cn/dashboard' };
        w.fetch = function () { return Promise.resolve({ ok: true }); };
        function XHR() { this._h = {}; }
        XHR.prototype.open = function (m, u) { this._m = m; this._u = u; };
        XHR.prototype.setRequestHeader = function (k, v) { this._h[k] = v; };
        XHR.prototype.send = function (b) { this._b = b; };
        new Function('window', 'XMLHttpRequest', 'location', script)(w, XHR, loc);
        return { window: w, XMLHttpRequest: XHR, location: loc };
    }

    const TARGET = '/billing/meter/get-user-resource';
    const captureOf = (env) => JSON.parse(env.window.__azhuCapture || 'null');

    // 1) fetch 命中目标 —— 四样东西都要抄下来
    {
        const env = makeEnv();
        env.window.fetch(TARGET, {
            method: 'POST',
            headers: { 'content-type': 'application/json', 'x-user-id': 'abc' },
            body: '{"page":1}'
        });
        const cap = captureOf(env);
        check(cap !== null, 'fetch 命中抓取目标 → 写入 __azhuCapture');
        check(!!cap && cap.url === TARGET, '抄到 URL');
        check(!!cap && cap.method === 'POST', '抄到方法');
        check(!!cap && cap.headers['content-type'] === 'application/json' && cap.headers['x-user-id'] === 'abc',
            '抄到请求头（普通对象形式）');
        check(!!cap && cap.body === '{"page":1}', '抄到 body');
    }

    // 2) 非目标请求：不能污染抓取结果，但必须进诊断列表
    {
        const env = makeEnv();
        env.window.fetch('/api/other');
        check(env.window.__azhuCapture === '', '负对照：非目标请求不写入 __azhuCapture（否则会抄错请求）');
        check(Array.isArray(env.window.__azhuSeen) && env.window.__azhuSeen.includes('/api/other'),
            '非目标请求进诊断列表（抄不到时靠它判断卡在哪一步）');
    }

    // 3) 只认第一个命中 —— 页面重复请求时不该覆盖先抄到的那份
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'GET' });
        env.window.fetch(TARGET, { method: 'DELETE' });
        const cap = captureOf(env);
        check(!!cap && cap.method === 'GET', '只记第一个命中（后续重复请求不覆盖）');
    }

    // 4) XHR 那条路（axios 之类走的就是它）
    {
        const env = makeEnv();
        const x = new env.XMLHttpRequest();
        x.open('POST', 'https://www.workbuddy.cn' + TARGET);
        x.setRequestHeader('x-user-id', 'xyz');
        x.send('{"p":2}');
        const cap = captureOf(env);
        check(!!cap && cap.url === 'https://www.workbuddy.cn' + TARGET, 'XHR 命中也会被抄（不只是 fetch）');
        check(!!cap && cap.headers['x-user-id'] === 'xyz', 'XHR 的 setRequestHeader 被收集');
        check(!!cap && cap.body === '{"p":2}', 'XHR 的 body 被抄到');
    }

    // 5) Headers 对象形式（Fetch API 的标准写法，脚本靠 forEach 分支处理）
    {
        const env = makeEnv();
        const h = new Headers();
        h.append('x-user-id', 'from-headers');
        env.window.fetch(TARGET, { method: 'POST', headers: h });
        const cap = captureOf(env);
        check(!!cap && cap.headers['x-user-id'] === 'from-headers', 'Headers 对象形式也能抄到');
    }

    // 6) 数组形式 [[k,v], ...]
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'POST', headers: [['x-user-id', 'arr']] });
        const cap = captureOf(env);
        check(!!cap && cap.headers['x-user-id'] === 'arr', '数组形式请求头也能抄到');
    }

    // 7) 诊断列表：去重、有上限
    {
        const env = makeEnv();
        for (let i = 0; i < 30; i++) env.window.fetch('/api/p' + (i % 5));
        check(env.window.__azhuSeen.length === 5, '诊断列表去重（同一路径只记一次）');
    }
    {
        const env = makeEnv();
        for (let i = 0; i < 30; i++) env.window.fetch('/api/p' + i);
        check(env.window.__azhuSeen.length === 20, '诊断列表有上限（不无限增长）');
    }

    // 8) 只记 path：带 token 的 query 绝不能出现在诊断列表里（那几行是要显示给用户看的）
    {
        const env = makeEnv('https://www.workbuddy.cn/dashboard?x=1');
        env.window.fetch('https://www.workbuddy.cn' + TARGET + '?token=SUPERSECRET');
        check(env.window.__azhuSeen.includes(TARGET), '诊断列表记的是 path');
        check(!env.window.__azhuSeen.some(p => String(p).includes('SUPERSECRET')),
            '负对照：query 里的 token 不出现在诊断列表里');
    }

    console.log('hook_check：PASS ' + pass + ' / FAIL ' + fail);
    return fail === 0 ? 0 : 1;
}

// 用 exitCode 而不是 process.exit()：stdout 接到管道时是异步写的，直接 exit 有截断风险。
process.exitCode = main();

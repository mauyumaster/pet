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
//   （包裹 fetch/XHR、挑出目标请求、相对地址绝对化、记录诊断列表），验不了浏览器强制的
//   规范行为 —— 最典型的是 **cookie 头**：真实浏览器禁止脚本设置/读取它，这里的桩不模拟
//   那条限制，所以「cookie 拿不到」在本文件里**测不出来**，那是宿主侧 CookieManager 的职责。
//   同理 user-agent / origin / referer 由浏览器自动添加，这里的桩只是「假装页面读得到
//   navigator.FOO」—— 它验的是「我们确实把页面读到的值抄进了 JSON」，不是「浏览器会发它」。
const fs = require('node:fs');

let pass = 0, fail = 0;
function check(ok, name) {
    if (ok) { pass++; console.log('[PASS] ' + name); } else { fail++; console.log('[FAIL] ' + name); }
}

async function main() {
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
    // ⚠ navigator 必须显式传进去：真浏览器里它是全局的，这里的 new Function 里没有。
    // ⚠ fetchStatus 是 2026-09-25 加的：桩必须能**回一个状态码**，否则「2xx 才抢定稿位」这条规则
    //   根本没法验 —— 而它正是「抄到的那个请求到底成功没有」的唯一来源。
    // ⚠ XHR 桩加了 addEventListener ＋ finishWith：真实 XHR 靠 loadend 事件交出状态码，
    //   桩不模拟就等于那条路径测不到（本文件存在的全部理由就是「注入的 JS 没人编译检查，只能真跑」）。
    function makeEnv(href, ua, lang, fetchStatus, uaData) {
        const w = {};
        const loc = { href: href || 'https://www.workbuddy.cn/dashboard' };
        const nav = { userAgent: ua === undefined ? 'Mozilla/5.0 (Test) Edg/153.0' : ua, language: lang === undefined ? 'zh-CN' : lang };
        nav.userAgentData = uaData === undefined
            ? { brands: [{ brand: 'Chromium', version: '140' }, { brand: 'Microsoft Edge', version: '140' }], mobile: false, platform: 'Windows' }
            : uaData;
        const st = fetchStatus === undefined ? 200 : fetchStatus;
        w.fetch = function () { return Promise.resolve({ ok: st >= 200 && st < 300, status: st }); };
        function XHR() { this._h = {}; this._ls = {}; this.status = 0; }
        XHR.prototype.open = function (m, u) { this._m = m; this._u = u; };
        XHR.prototype.setRequestHeader = function (k, v) { this._h[k] = v; };
        XHR.prototype.addEventListener = function (t, f) { this._ls[t] = f; };
        XHR.prototype.send = function (b) { this._b = b; };
        XHR.prototype.finishWith = function (code) {      // 测试用：模拟响应到达
            this.status = code;
            const f = this._ls['loadend'];
            if (f) f.call(this);
        };
        new Function('window', 'XMLHttpRequest', 'location', 'navigator', script)(w, XHR, loc, nav);
        return { window: w, XMLHttpRequest: XHR, location: loc };
    }

    // 让已排队的微任务全部跑完 —— 定稿是在响应的 .then 里完成的，同步断言看不到。
    const flush = () => new Promise(r => setTimeout(r, 0));

    const TARGET = '/billing/meter/get-user-resource';
    const ABS = 'https://www.workbuddy.cn' + TARGET;
    const captureOf = (env) => JSON.parse(env.window.__azhuCapture || 'null');

    // 1) fetch 命中目标 —— 六样东西都要抄下来（URL/方法/头/body ＋ 页面上下文三样）
    {
        const env = makeEnv();
        env.window.fetch(TARGET, {
            method: 'POST',
            headers: { 'content-type': 'application/json', 'x-user-id': 'abc' },
            body: '{"page":1}'
        });
        const cap = captureOf(env);
        check(cap !== null, 'fetch 命中抓取目标 → 写入 __azhuCapture');
        check(!!cap && cap.url === ABS, '抄到 URL，且相对地址被解析成绝对（否则 HttpClient 会抛「相对 URI 不允许」）');
        check(!!cap && cap.method === 'POST', '抄到方法');
        check(!!cap && cap.headers['content-type'] === 'application/json' && cap.headers['x-user-id'] === 'abc',
            '抄到请求头（普通对象形式）');
        check(!!cap && cap.body === '{"page":1}', '抄到 body');
    }

    // 2) 页面上下文 —— 这是「补上浏览器自动头」的唯一来源，缺了就只能靠猜
    {
        const env = makeEnv('https://www.workbuddy.cn/dashboard', 'Mozilla/5.0 (X) Chrome/153', 'zh-CN');
        env.window.fetch(TARGET);
        const cap = captureOf(env);
        check(!!cap && cap.ua === 'Mozilla/5.0 (X) Chrome/153', '抄到 navigator.userAgent（补 user-agent 用的真值，不是猜的）');
        check(!!cap && cap.href === 'https://www.workbuddy.cn/dashboard', '抄到当前页 href（补 referer 用）');
        check(!!cap && cap.org === 'https://www.workbuddy.cn', '抄到页面 origin');
        check(!!cap && cap.lang === 'zh-CN', '抄到 navigator.language');
    }

    // 3) 当前页的 query 不能进凭据文件（它可能带 token；而钩子只记 path 的规则管不到 href 这个字段）
    {
        const env = makeEnv('https://www.workbuddy.cn/dashboard?sid=SUPERSECRET#frag');
        env.window.fetch(TARGET);
        const cap = captureOf(env);
        check(!!cap && !String(cap.href).includes('SUPERSECRET'), '负对照：当前页 query 里的 token 不进 href 字段');
        check(!!cap && !String(cap.href).includes('#'), 'href 里的 fragment 也被切掉');
    }

    // 4) 非目标请求：不能污染抓取结果，但必须进诊断列表
    {
        const env = makeEnv();
        env.window.fetch('/api/other');
        check(env.window.__azhuCapture === '', '负对照：非目标请求不写入 __azhuCapture（否则会抄错请求）');
        check(Array.isArray(env.window.__azhuSeen) && env.window.__azhuSeen.includes('/api/other'),
            '非目标请求进诊断列表（抄不到时靠它判断卡在哪一步）');
    }

    // 5) 定稿与覆盖规则（2026-09-25 改）：第一个命中先占位；响应回来后**只有 2xx 才抢走定稿位**。
    //    旧规则是「第一个命中永远不让位」—— 可页面刚打开时的第一个命中很可能是一次失败请求，
    //    于是抄到的永远是那份失败样本：回测必然 401，而现场看上去「页面上余额明明显示着」。
    {
        const env = makeEnv();                       // 桩默认回 200
        env.window.fetch(TARGET, { method: 'GET' });
        const first = captureOf(env);
        check(!!first && first.method === 'GET', '第一发命中后立刻占位（不等响应）');
        check(!!first && first.status === 0, '占位时 status=0（＝响应还没回来，宿主不得据此下结论）');
        await flush();
        const cap = captureOf(env);
        check(!!cap && cap.method === 'GET' && cap.status === 200, '2xx 回来后在定稿里带上真实状态码');
    }
    {
        const env = makeEnv(undefined, undefined, undefined, 401);   // 桩一律回 401
        env.window.fetch(TARGET, { method: 'GET' });
        await flush();
        env.window.fetch(TARGET, { method: 'PUT' });                 // 后来的失败请求
        await flush();
        const cap = captureOf(env);
        check(!!cap && cap.method === 'GET' && cap.status === 401,
            '全是 4xx 时：定稿仍留在第一个样本上，**但状态码要回填** —— 宿主就靠它说「网站自己发这个请求也 401」');
    }
    {
        const env = makeEnv(undefined, undefined, undefined, 200);
        env.window.fetch(TARGET, { method: 'GET' });
        await flush();
        env.window.fetch(TARGET, { method: 'DELETE' });              // 后到的成功请求
        await flush();
        check(captureOf(env).method === 'DELETE',
            '后到的 2xx 抢走定稿位（它才是「网站自己成功取到数」的那一条）');
    }
    {
        const env = makeEnv();
        const x = new env.XMLHttpRequest();
        x.open('POST', ABS);
        x.send('{}');
        check(captureOf(env).status === 0, 'XHR 未收到响应前 status=0');
        x.finishWith(500);
        check(captureOf(env).status === 500, 'XHR 的 loadend 把状态码回填进定稿（不只是 fetch 那条路）');
    }

    // 6) XHR 那条路（axios 之类走的就是它）
    {
        const env = makeEnv();
        const x = new env.XMLHttpRequest();
        x.open('POST', ABS);
        x.setRequestHeader('x-user-id', 'xyz');
        x.send('{"p":2}');
        const cap = captureOf(env);
        check(!!cap && cap.url === ABS, 'XHR 命中也会被抄（不只是 fetch）');
        check(!!cap && cap.headers['x-user-id'] === 'xyz', 'XHR 的 setRequestHeader 被收集');
        check(!!cap && cap.body === '{"p":2}', 'XHR 的 body 被抄到');
    }

    // 7) Headers 对象形式（Fetch API 的标准写法，脚本靠 forEach 分支处理）
    {
        const env = makeEnv();
        const h = new Headers();
        h.append('x-user-id', 'from-headers');
        env.window.fetch(TARGET, { method: 'POST', headers: h });
        const cap = captureOf(env);
        check(!!cap && cap.headers['x-user-id'] === 'from-headers', 'Headers 对象形式也能抄到');
    }

    // 8) 数组形式 [[k,v], ...]
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'POST', headers: [['x-user-id', 'arr']] });
        const cap = captureOf(env);
        check(!!cap && cap.headers['x-user-id'] === 'arr', '数组形式请求头也能抄到');
    }

    // 9) 诊断列表：去重、有上限
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

    // 10) 只记 path：带 token 的 query 绝不能出现在诊断列表里（那几行是要显示给用户看的）
    {
        const env = makeEnv('https://www.workbuddy.cn/dashboard?x=1');
        env.window.fetch('https://www.workbuddy.cn' + TARGET + '?token=SUPERSECRET');
        check(env.window.__azhuSeen.includes(TARGET), '诊断列表记的是 path');
        check(!env.window.__azhuSeen.some(p => String(p).includes('SUPERSECRET')),
            '负对照：query 里的 token 不出现在诊断列表里');
        check(String(captureOf(env).url).includes('token=SUPERSECRET'),
            '但抓取目标的 URL 保留 query（接口靠它传参，丢了请求就是错的）');
    }

    // 11) 客户端提示（2026-09-25 加）：现场是「同一套 cookie、同一组请求头，浏览器 200、我们 401」。
    //     cookie 与头都排除了之后，剩下的变量里有「UA 说自己是 Edge、却不带 sec-ch-ua」这种
    //     指纹不一致 —— 而 userAgentData 是页面侧真值，抄下来才是「抄」，不能自己编。
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'POST' });
        const cap = captureOf(env);
        check(!!cap && cap.chua === '"Chromium";v="140", "Microsoft Edge";v="140"',
            '客户端提示 sec-ch-ua 按 brand;v= 的真实格式抄下来');
        check(!!cap && cap.chuam === '?0' && cap.chuap === '"Windows"',
            '客户端提示的 mobile / platform 也一起抄（缺一项就是一份自相矛盾的指纹）');
    }
    // 负对照：没有 userAgentData 的浏览器（老内核）→ 三项为空串，且钩子照样装得上。
    // ⚠ 这条是必须的：ch() 在**注入时**就跑，它若抛异常，整个钩子都不会装 —— 而那种失败
    //   在现象上与「页面压根不发请求」一模一样，正是本项目记录过的「静默失效」。
    {
        const env = makeEnv(undefined, undefined, undefined, undefined, null);
        env.window.fetch(TARGET, { method: 'POST' });
        const cap = captureOf(env);
        check(cap !== null && cap.chua === '' && cap.chuam === '' && cap.chuap === '',
            '负对照：没有 userAgentData 时三项为空串（不编值，也不因此装不上钩子）');
    }

    // 12) body 只抄**字符串**（2026-09-25 加）。
    //     ⚠ 此前是 `''+b`：Session/FormData/对象一律被拼成 "[object FormData]" 这种字符串。
    //     以前它无害（body 没人发），但从「body 原样上路」那一笔起，它会被**真的发出去** ——
    //     「抄不到就留空」比「抄到一个假的事实」安全得多。
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'POST', body: { a: 1 } });
        check(captureOf(env).body === '', '负对照：非字符串 body 抄成空串，而不是 "[object Object]"');
    }
    {
        const env = makeEnv();
        env.window.fetch(TARGET, { method: 'POST', body: '' });
        check(captureOf(env).body === '', '空字符串 body 也是空串（回测那边据此退回空 JSON）');
    }

    console.log('hook_check：PASS ' + pass + ' / FAIL ' + fail);
    return fail === 0 ? 0 : 1;
}

// ⚠ main 是 async（2026-09-25）：新增用例要等微任务跑完，才看得到「响应回来之后定稿成什么」。
// 用 exitCode 而不是 process.exit()：stdout 接到管道时是异步写的，直接 exit 有截断风险。
main().then(c => { process.exitCode = c; });

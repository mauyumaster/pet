// --webtest：验「内嵌浏览器这条依赖链在本机真的能用」。
//
// ⚠ 为什么非要有这么一条**能自动跑**的判据：WebView2 = 托管 DLL ＋ 原生加载器（WebView2Loader.dll）
//   ＋ 系统运行时三者的组合，而本项目是**单文件发布**（PublishSingleFile=true）。
//   原生库在单文件布局下解析失败时，**编译零错误、其它测试全绿、只是运行到那一刻才炸** ——
//   正是「编译通过、运行才炸」那一类坑，只能真跑一次才知道。
//   （对照：这条判据是 2026-09-25 引入 WebView2 时加的，加它的理由与 .cmd 行尾那条同源。）
//
// ⚠⚠ 证据边界（别把这条当成「登录功能验过了」）：本测试**不开窗口、不登录、不联网**，
//   只验到「系统运行时在 ＋ 原生加载器解得开 ＋ 环境对象能创建」。
//   真正的登录取凭据流程仍然只有人眼能验 —— 见 CredentialBrowserWindow 顶部的注释。
//
// ⚠⚠ 想验「**分发形态**到底带没带加载器」，必须**在收窄 PATH 的环境里跑** ——
//   本机 PATH 上恰好有一份 WebView2Loader.dll（Windows Performance Toolkit 自带），
//   在正常环境里跑，产物目录里一个字节都没有也照样 PASS。可复制的做法：
//     python -c "import os,subprocess;e=dict(os.environ);e['PATH']=r'C:\Windows\System32';
//                subprocess.run([r'D:\path\to\pet.exe','--webtest'],env=e)"
//   2026-09-25 正是这样量出「zip 与安装器都漏发了这个文件」的（详见下面那一格的注释）。
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace AzhuPet
{
    internal static class WebTest
    {
        public static int Run()
        {
            int pass = 0, fail = 0;
            Console.WriteLine("==== webtest：内嵌浏览器依赖链 ====");
            // 「打包形态」用 pet.dll 在不在来判断：单文件发布只有一个 exe，没有同名 dll。
            // 不读 Assembly.Location —— 那东西在单文件下恒为空串（还会带一条 IL3000 警告）。
            bool loose = File.Exists(Path.Combine(AppContext.BaseDirectory, "pet.dll"));
            Console.WriteLine("打包形态：" + (loose ? "松散（dll 与 exe 分家）" : "单文件（PublishSingleFile）"));
            Console.WriteLine("运行目录：" + AppContext.BaseDirectory);

            string loader = FindLoader();

            string ver = null;
            try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 取 WebView2 运行时版本失败：" + ex.GetType().Name + " " + ex.Message);
                fail++;
            }
            if (ver != null) { Console.WriteLine("[PASS] 本机 WebView2 运行时版本 = " + ver); pass++; }

            // 真去建一个环境对象：这一步才会真正 P/Invoke 到原生加载器 —— 这是唯一的权威判据，
            // 前一条（取版本号）与下面的「文件在不在」都只是旁证。
            string dir = Path.Combine(Path.GetTempPath(), "AzhuPet-WebTest-" + Guid.NewGuid().ToString("N"));
            bool envOk = false;
            try
            {
                var env = CoreWebView2Environment.CreateAsync(null, dir).GetAwaiter().GetResult();
                envOk = env != null;
                if (!envOk) Console.WriteLine("[FAIL] 创建 WebView2 环境返回了 null");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 创建 WebView2 环境失败：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("       ⇒ 内嵌浏览器在本机用不了；面板里的「手工粘贴」那条路仍然可用。");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
            Check(envOk, "WebView2 环境能创建（原生加载器真的被解析到了）", ref pass, ref fail);

            // ⚠⚠ 「加载器随产物分发」这一格 —— 它被我自己判松过一次，教训留在这里（2026-09-25）：
            //   当时的推理是「单文件发布把原生加载器打进了 exe，旁边没有它属正常」，依据是
            //   「把便携 zip 解压到干净目录再跑，环境能创建 = PASS」。**那个依据是错的**：
            //   把 PATH 收窄到只有 System32 再跑同一份东西，立刻 DllNotFoundException ——
            //   它是被本机 PATH 上**别处的一份**加载器救活的（这台机器的
            //   C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\ 里恰好有一个）。
            //   ⇒ 「产物目录干净」不等于「环境干净」；拿「环境能创建」当分发的证据，会在
            //     「开发机上有、干净机器上没有」时骗过所有人 —— 而 zip 里那几个文件正好漏了它。
            //   ⚠ 另一次更早的错法：扫 exe 字节找 "WebView2Loader.dll" 当作「已打进 exe」的证据。
            //     那也是假的 —— 这个名字本来就以 DllImport 元数据的形式存在于
            //     Microsoft.Web.WebView2.Core.dll 里，而单文件布局会把那个托管程序集打进去
            //     ⇒ 扫描**恒为真**（实测：Debug 的 pet.dll 里没有，WebView2.Core.dll 里有）。
            //   ⇒ 所以这一格**一律看文件在不在**（exe 旁边或 runtimes/<rid>/native 都算），
            //     单文件布局也不例外。环境能不能建仍然单独报一条 —— 它能抓另一类问题
            //     （文件在，但加载不起来：位数不对、依赖缺）。
            if (loader != null)
            {
                pass++;
                Console.WriteLine("[PASS] WebView2Loader.dll 随产物分发（" + Rel(loader) + "）");
            }
            else
            {
                fail++;
                Console.WriteLine("[FAIL] 产物里没有 WebView2Loader.dll（exe 旁边与 runtimes/<rid>/native 下都没有）");
                if (envOk)
                    Console.WriteLine("       ⚠ 但环境偏偏建起来了 ⇒ 多半是本机别处有一份（PATH 上的目录也算）。"
                        + "**在干净机器上这里会 DllNotFoundException**，别把它当通过。");
            }

            Console.WriteLine("webtest：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        private static string Rel(string path)
        {
            try { return Path.GetRelativePath(AppContext.BaseDirectory, path); } catch { return path; }
        }

        /// <summary>找旁边的原生加载器。⚠ 位置随布局变，所以**递归找**而不是钉死根目录：
        /// 松散构建只放在 runtimes/&lt;rid&gt;/native/（靠 deps.json 解析），单文件发布才放在 exe 旁边。
        /// ⚠ 返回 null **不等于出错** —— 单文件布局下加载器被打了进 exe，本来就不在文件系统里
        /// （能不能用由「环境能创建」判定）。第一版判据把 null 当失败，于是在一个完全能用的布局上报红。</summary>
        private static string FindLoader()
        {
            try
            {
                string root = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
                if (File.Exists(root)) return root;
                string rt = Path.Combine(AppContext.BaseDirectory, "runtimes");
                if (Directory.Exists(rt))
                {
                    // ⚠ 递归找会**先撞上别的架构**（这包里同时有 win-x64 / win-x86 / win-arm64 三份），
                    //   报出 win-arm64 的路径同样属于「判据不准」。先按本进程架构挑，挑不到再退回任意一份。
                    string want = "win-" + RidArch();
                    string any = null;
                    foreach (string p in Directory.GetFiles(rt, "WebView2Loader.dll", SearchOption.AllDirectories))
                    {
                        if (any == null) any = p;
                        if (p.Replace('\\', '/').IndexOf("/" + want + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                            return p;
                    }
                    if (any != null) return any;
                }
            }
            catch { }
            return null;
        }

        private static string RidArch()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X86: return "x86";
                case Architecture.Arm64: return "arm64";
                default: return "x64";
            }
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("[PASS] " + name); }
            else { fail++; Console.WriteLine("[FAIL] " + name); }
        }
    }
}

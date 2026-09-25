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
using System;
using System.IO;
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

            // ⚠ 位置随布局变，所以**递归找**而不是钉死根目录：
            //   单文件发布把它放在 exe 旁边；松散构建只放在 runtimes/<rid>/native/（靠 deps.json 解析）。
            //   第一版判据写死了根目录，于是在一个「实际完全能用」的布局上报红 —— 判据太严也是一种错。
            string loader = FindLoader();
            if (loader == null) Console.WriteLine("[FAIL] WebView2Loader.dll 不在产物目录里（根目录与 runtimes/ 下都没有）");
            Check(loader != null, "WebView2Loader.dll 随产物分发（根目录或 runtimes/<rid>/native）", ref pass, ref fail);

            string ver = null;
            try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 取 WebView2 运行时版本失败：" + ex.GetType().Name + " " + ex.Message);
                fail++;
            }
            if (ver != null) { Console.WriteLine("[PASS] 本机 WebView2 运行时版本 = " + ver); pass++; }

            // 真去建一个环境对象：这一步才会真正 P/Invoke 到原生加载器 —— 前面两条都只是「文件在不在」。
            string dir = Path.Combine(Path.GetTempPath(), "AzhuPet-WebTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var env = CoreWebView2Environment.CreateAsync(null, dir).GetAwaiter().GetResult();
                Check(env != null, "WebView2 环境能创建（原生加载器真的被解析到了）", ref pass, ref fail);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 创建 WebView2 环境失败：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("       ⇒ 内嵌浏览器在本机用不了；面板里的「手工粘贴」那条路仍然可用。");
                fail++;
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine("webtest：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        private static string FindLoader()
        {
            try
            {
                string root = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
                if (File.Exists(root)) return root;
                string rt = Path.Combine(AppContext.BaseDirectory, "runtimes");
                if (Directory.Exists(rt))
                    foreach (string p in Directory.GetFiles(rt, "WebView2Loader.dll", SearchOption.AllDirectories)) return p;
            }
            catch { }
            return null;
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("[PASS] " + name); }
            else { fail++; Console.WriteLine("[FAIL] " + name); }
        }
    }
}

// 通用「余额源」配置模型：在 balances.json 里加一条源，桌宠的气泡就多一行，零改代码。
// 字段路径表达式例子：
//   扁平数值          total_balance                                   → 取该字段
//   嵌套再求和         data.Response.Data.Accounts[CapacityType=1].CapacityRemain
//   两字段相减         sub:data.Response.Data.total_amount;data.Response.Data.consumed_amount
// 鉴权与 cookie    通过 SecretFile 引用一个本地 .txt（其它静态头放 HeadersText）。
//   该 txt 格式与现有凭据文件一致：若干「名字: 值」行，可选 `---body---` 段放请求体。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AzhuPet
{
    /// <summary>单个余额源。JSON 序列化的就是这个类。</summary>
    public sealed class BalanceSource
    {
        public string Name = "";              // 气泡显示名，如 "WorkBuddy 积分"
        public string Url = "";               // 接口完整 URL
        public string Method = "POST";        // GET / POST
        public string PathExpr = "";          // 取数表达式，见文件头部说明
        public string Unit = "";              // 显示单位；空则不追加
        public string HeadersText = "";       // 静态请求头，逐行「名字: 值」
        public string SecretFile = "";        // 可选：引用本机凭据文件(含 cookie/authorization)，
                                              //   支持相对(C:/ 绝/相对 exe 目录)与纯文件名(走 SecretDir)
        public string Body = "";              // 可选：覆盖请求体；空则用 SecretFile 的 ---body--- 段，都没有则 '{}'
        public bool Enabled = true;
    }

    /// <summary>一个源取数后的一行结果。</summary>
    public sealed class BalanceCell
    {
        public string Name;
        public double Value = double.NaN;
        public string Unit;
        public bool Ok;
        public string Error;
    }

    /// <summary>balances.json 的加载/保存。默认不存在时返回一条占位示例，供面板首启编辑。</summary>
    public static class BalanceSources
    {
        public static string StorageDir() => StatusProbe.SecretDir();

        /// <summary>配置只有一个真值：本机 LocalAppData（或 AZHU_SECRET_DIR）。
        /// 不再把工程目录中的 balances.json 当保存目标，避免同步上云和“改了 A、程序读 B”。</summary>
        public static string ConfigPath()
        {
            return Path.Combine(StorageDir(), "balances.json");
        }

        public static DateTime ConfigWriteUtc()
        {
            string path = ConfigPath();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        public static List<BalanceSource> Load()
        {
            List<BalanceSource> list;
            string error;
            return TryLoad(out list, out error) ? list : DefaultList();
        }

        /// <summary>给设置面板的非静默加载。坏 JSON 必须明确报错，不能显示空列表后又把原文件覆盖掉。</summary>
        public static bool TryLoad(out List<BalanceSource> list, out string error)
        {
            list = DefaultList();
            error = null;
            string path = ConfigPath();
            if (!File.Exists(path)) return true;
            try
            {
                var opts = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                    IncludeFields = true // BalanceSource 是字段模型；漏掉它会把每条序列化成空对象 {}
                };
                var loaded = JsonSerializer.Deserialize<List<BalanceSource>>(File.ReadAllText(path), opts);
                list = loaded ?? DefaultList();
                list.RemoveAll(s => s == null || (string.IsNullOrWhiteSpace(s.Name) && string.IsNullOrWhiteSpace(s.Url)));
                foreach (var s in list) Normalize(s);
                return true;
            }
            catch (Exception ex)
            {
                error = "balances.json 无法读取：" + ex.Message;
                return false;
            }
        }

        public static void Save(List<BalanceSource> list, string path)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            path = string.IsNullOrWhiteSpace(path) ? ConfigPath() : path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            foreach (var s in list) Normalize(s);
            var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            string temp = path + ".tmp";
            string backup = path + ".bak";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, opts));
            if (File.Exists(path)) File.Copy(path, backup, true);
            File.Move(temp, path, true);
        }

        public static List<string> Validate(BalanceSource s)
        {
            var errors = new List<string>();
            if (s == null) { errors.Add("余额源为空"); return errors; }
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add("请填写显示名称");
            Uri uri;
            if (!Uri.TryCreate(s.Url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                errors.Add("URL 必须是完整的 http/https 地址");
            string method = (s.Method ?? "").Trim().ToUpperInvariant();
            if (method != "GET" && method != "POST") errors.Add("请求方法只能是 GET 或 POST");
            if (string.IsNullOrWhiteSpace(s.PathExpr)) errors.Add("请填写余额字段路径");
            if (!string.IsNullOrWhiteSpace(s.SecretFile) && !File.Exists(ResolveSecret(s.SecretFile)))
                errors.Add("找不到凭据文件：" + s.SecretFile);
            return errors;
        }

        public static bool HasSensitiveInlineHeaders(BalanceSource s)
        {
            string h = s?.HeadersText ?? "";
            return h.Split('\n', '\r').Any(line =>
            {
                int i = line.IndexOf(':');
                if (i <= 0) return false;
                string name = line.Substring(0, i).Trim();
                return name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>解析源引用的凭据文件：读出请求头与请求体。文件不存在返回 (headers空, body空)。</summary>
        public static (string headers, string body) ReadSecret(string file)
        {
            string abs = ResolveSecret(file);
            if (abs == null || !File.Exists(abs)) return (string.Empty, string.Empty);
            var headers = new List<string>();
            var body = new List<string>();
            bool inBody = false;
            foreach (string raw in File.ReadAllLines(abs))
            {
                string t = raw.Trim();
                if (t.StartsWith("---body", StringComparison.Ordinal)) { inBody = true; continue; }
                if (t.StartsWith("---", StringComparison.Ordinal)) { continue; }
                if (inBody) { body.Add(raw); continue; }
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                headers.Add(raw);
            }
            return (string.Join("\n", headers), string.Join("\n", body));
        }

        /// <summary>SecretFile 解析到绝对路径：支持纯文件名(SecretDir→退回 exe 回溯)、绝对/相对路径。</summary>
        public static string ResolveSecret(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            // 绝对路径
            if (Path.IsPathRooted(file)) return Path.GetFullPath(file);
            // 相对办公目录（含子目录）→ 相对 exe 目录
            if (file.Contains('/') || file.Contains('\\'))
            {
                string rel = Path.Combine(AppContext.BaseDirectory, file);
                if (File.Exists(rel)) return rel;
            }
            // 纯文件名 → SecretDir 优先，再 exe 向上回溯
            string local = Path.Combine(StorageDir(), file);
            if (File.Exists(local)) return local;
            string dir0 = Path.Combine(AppContext.BaseDirectory, "Secrets");
            if (File.Exists(Path.Combine(dir0, file))) return Path.Combine(dir0, file);
            string d = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string p = Path.Combine(d, file);
                if (File.Exists(p)) return p;
                var up = Directory.GetParent(d);
                if (up == null) break;
                d = up.FullName;
            }
            return local; // 返回首选落点，便于 UI 给出可执行的提示
        }

        public static void SaveSecret(string fileName, string content)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("凭据文件名为空");
            if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("凭据文件名不能包含路径");
            Directory.CreateDirectory(StorageDir());
            string path = Path.Combine(StorageDir(), fileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, (content ?? "").Trim() + Environment.NewLine);
            File.Move(temp, path, true);
        }

        private static void Normalize(BalanceSource s)
        {
            s.Name = (s.Name ?? "").Trim();
            s.Url = (s.Url ?? "").Trim();
            s.Method = string.IsNullOrWhiteSpace(s.Method) ? "GET" : s.Method.Trim().ToUpperInvariant();
            s.PathExpr = (s.PathExpr ?? "").Trim();
            s.Unit = (s.Unit ?? "").Trim();
            s.HeadersText = (s.HeadersText ?? "").Trim();
            s.SecretFile = (s.SecretFile ?? "").Trim();
            s.Body = (s.Body ?? "").Trim();
        }

        private static List<BalanceSource> DefaultList()
        {
            // 默认没有自定义源；用右键「余额源设置…」新增。字段路径语法见文件头部说明。
            return new List<BalanceSource>();
        }
    }
}

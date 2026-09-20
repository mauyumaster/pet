// 取数路径引擎：把 balances.json 里的 PathExpr 翻译成 JSON 里的一个数值。
// 语法：
//   扁平数值        total_balance
//   嵌套求和        data.Response.Data.Accounts[CapacityType=1].CapacityRemain
//   两值相减        sub:data.Response.Data.total_amount;data.Response.Data.consumed_amount
//   . = 下钻对象字段；[k=v] = 数组过滤；末尾数值取 double；过滤产生的多个匹配默认求和。
// 解析均容错：缺对象→跳过该候选；数字字符串也能读(与主库 IntEl 同 philosophy)。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AzhuPet
{
    public static class PathJsonExtract
    {
        /// <summary>解析表达式并求值。失败/无匹配返回 double.NaN。</summary>
        public static double Eval(string expr, JsonDocument doc)
        {
            if (string.IsNullOrWhiteSpace(expr) || doc == null) return double.NaN;
            try
            {
                expr = expr.Trim();
                // 相减： sub:路径A;路径B
                if (expr.StartsWith("sub:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = expr.Substring(4).Split(new[] { ';' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return double.NaN;
                    double a = Sum(Collect(doc.RootElement, parts[0]));
                    double b = Sum(Collect(doc.RootElement, parts[1]));
                    return (double.IsNaN(a) || double.IsNaN(b)) ? double.NaN : a - b;
                }
                // 默认：求和（单个也是它自己）
                return Sum(Collect(doc.RootElement, expr));
            }
            catch { return double.NaN; }
        }

        public static double Sum(List<double> vals)
        {
            double s = 0;
            bool any = false;
            for (int i = 0; i < vals.Count; i++) { s += vals[i]; any = true; }
            return any ? s : double.NaN;
        }

        /// <summary>从 root 出发按路径收集所有匹配的数值（数组过滤后可能多个）。</summary>
        private static List<double> Collect(JsonElement root, string path)
        {
            var segs = Tokenize(path);
            var result = new List<double>();
            Walk(root, segs, 0, result);
            return result;
        }

        private static List<string> Tokenize(string path)
        {
            var segs = new List<string>();
            int i = 0, n = path.Length;
            while (i < n)
            {
                // 数组过滤 [k=v]
                if (path[i] == '[')
                {
                    int end = path.IndexOf(']', i);
                    if (end < 0) break;
                    segs.Add(path.Substring(i, end - i + 1));
                    i = end + 1;
                    if (i < n && path[i] == '.') i++;
                    continue;
                }
                int dot = path.IndexOf('.', i);
                int brk = path.IndexOf('[', i);
                int endSeg = -1;
                if (dot < 0 && brk < 0) endSeg = n;
                else if (dot < 0) endSeg = brk;
                else if (brk < 0) endSeg = dot;
                else endSeg = Math.Min(dot, brk);
                string seg = path.Substring(i, endSeg - i).Trim();
                if (seg.Length > 0) segs.Add(seg);
                i = endSeg;
                if (i < n && path[i] == '.') i++;
            }
            return segs;
        }

        private static void Walk(JsonElement node, List<string> segs, int idx, List<double> outList)
        {
            if (idx >= segs.Count)
            {
                if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out double d)) outList.Add(d);
                else if (node.ValueKind == JsonValueKind.String && double.TryParse(node.GetString(),
                         NumberStyles.Float, CultureInfo.InvariantCulture, out double ds)) outList.Add(ds);
                return;
            }
            string seg = segs[idx];
            // 数组过滤 [k=v]
            if (seg.StartsWith("[", StringComparison.Ordinal) && seg.EndsWith("]", StringComparison.Ordinal))
            {
                if (node.ValueKind != JsonValueKind.Array) return;
                string inner = seg.Substring(1, seg.Length - 2);
                int eq = inner.IndexOf('=');
                if (eq < 0) return;
                string k = inner.Substring(0, eq).Trim();
                string v = inner.Substring(eq + 1).Trim();
                foreach (var el in node.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object
                        && el.TryGetProperty(k, out var pv) && Matches(pv, v))
                        Walk(el, segs, idx + 1, outList);
                return;
            }
            // 对象字段
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(seg, out var child))
                Walk(child, segs, idx + 1, outList);
        }

        private static bool Matches(JsonElement pv, string want)
        {
            string got;
            switch (pv.ValueKind)
            {
                case JsonValueKind.Number:
                    got = pv.GetRawText().TrimStart('-').Trim('"');
                    break;
                case JsonValueKind.String:
                    got = pv.GetString();
                    break;
                default: return false;
            }
            return string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
        }
    }
}
// 每小时工作总结（2026-09-20，用户拍板三条：真模型生成 / 落 Obsidian 库 / 按「开机后每满 60 分钟」触发）。
//
// 数据源：memory.jsonl（她自己的观察记录，%LOCALAPPDATA%\AzhuPet\）。
// 落点：`{库}\40 Projects\阿助（桌宠）/她写的/YYYY-MM-DD.md`（按天一个文件、按小时分节）——
//   与内部状态刻意分开的那个「给你看的产出」落点（见 Memory.cs 文件头）。
//
// ⚠⚠ 隐私边界与台词 prompt 相同：发给模型的只有**应用名＋时长＋切换次数**（元数据），
//   不发窗口标题、不发屏幕文字。想要「内容级」总结需要另一条权限，本轮没做（风险页记）。
//
// ⚠ 时长归属的算法：memory.jsonl 每行记的是「进入 proc 时，**上一个**窗口待了 prevDwell 秒」
//   ⇒ prevDwell 归给**前一行**的 proc。三类行要特殊处理：
//     manual 行  —— 不是切换（prevDwill 恒 0、proc 来自另一个口径）⇒ 跳过且**不推进**前一行指针；
//     roast 行   —— prevDwell 是当前应用的累计，不是切换 ⇒ 不归属（reason 前缀 roast）；
//     窗口内第一行 —— 它的 prevDwell 属于窗口外的应用 ⇒ 丢弃归属（误差 ≤ 一次切换）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AzhuPet
{
    internal static class HourlySummary
    {
        public sealed class Stats
        {
            public int Switches;               // 窗口内的换应用次数（含被闸门拦下的那次 —— 那也是真实切换）
            public int Lines;                  // 窗口内的总行数（诊断用）
            public readonly Dictionary<string, double> SecByProc =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>**纯函数**：到点了吗。（用户拍板：按开机后每满 interval 分钟，不对齐整点。）</summary>
        public static bool Due(DateTime now, DateTime lastAt, double intervalMin)
        {
            if (lastAt == DateTime.MinValue) return true;      // 从没总结过 ⇒ 到点
            return (now - lastAt).TotalMinutes >= intervalMin;
        }

        private static DateTime ParseAt(JsonElement e)
        {
            return DateTime.ParseExact(e.GetString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Str(Dictionary<string, JsonElement> row, string key)
        {
            JsonElement e;
            return row.TryGetValue(key, out e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }

        /// <summary>
        /// **纯函数**：把窗口内的记忆行聚合成统计。全部输入可钉死 ⇒ 判据离线可打。
        /// </summary>
        public static Stats Aggregate(List<Dictionary<string, JsonElement>> rows, DateTime from, DateTime to)
        {
            var s = new Stats();
            string lastProc = null;
            bool firstInWindow = true;
            foreach (var row in rows)
            {
                DateTime at;
                try { at = ParseAt(row["at"]); }
                catch { continue; }                                 // 坏行已在 ReadAll 计数，这里静默跳过
                if (at < from || at > to) continue;
                s.Lines++;

                string proc = Str(row, "proc");
                string reason = Str(row, "reason");
                bool manual = row.ContainsKey("manual") && row["manual"].ValueKind == JsonValueKind.True;
                bool roast = reason != null && reason.StartsWith("roast");

                double dwell = 0;
                JsonElement d;
                if (row.TryGetValue("prevDwell", out d) && d.ValueKind == JsonValueKind.Number)
                    dwell = d.GetDouble();

                if (!manual && !roast && !firstInWindow && !string.IsNullOrEmpty(lastProc)
                    && !string.IsNullOrEmpty(proc))
                {
                    s.Switches++;
                    if (!s.SecByProc.ContainsKey(lastProc)) s.SecByProc[lastProc] = 0;
                    s.SecByProc[lastProc] += dwell;
                }
                if (firstInWindow) firstInWindow = false;           // 窗口内第一行：丢弃归属（见文件头）
                if (!manual) lastProc = proc;                       // manual 行不推进指针（文件头）
            }
            return s;
        }

        /// <summary>**纯函数**：喂给模型的总结请求。</summary>
        public static string BuildPrompt(Stats s, DateTime from, DateTime to)
        {
            var sb = new StringBuilder();
            sb.Append("以下是他在 ").Append(from.ToString("HH:mm")).Append("–").Append(to.ToString("HH:mm"))
              .Append(" 这段时间用电脑的记录（应用 → 大约时长），期间切换应用 ").Append(s.Switches).Append(" 次：\n");
            var list = new List<KeyValuePair<string, double>>(s.SecByProc);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in list)
                sb.Append("- ").Append(StubSpeaker.Human(kv.Key)).Append(" ")
                  .Append((kv.Value / 60.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" 分钟\n");
            if (list.Count == 0) sb.Append("- （这一段没有记录）\n");
            sb.Append("请用两三句话写一段这一小时他做了什么的小结，像朋友替他随手记的日记；")
              .Append("不要罗列数据，不要分点，不要标题。");
            return sb.ToString();
        }

        /// <summary>**纯函数**：落盘文件的路径（按天一个）。</summary>
        public static string FilePath(string vaultPath, DateTime day)
        {
            return Path.Combine(vaultPath, "40 Projects", "阿助（桌宠）", "她写的",
                day.ToString("yyyy-MM-dd") + ".md");
        }

        /// <summary>**纯函数**：这一节写进文件的样子（模型文本＋数据脚注）。失败时也要有可读原因。</summary>
        public static string BuildSection(string summary, string error, Stats s, DateTime from, DateTime to)
        {
            var sb = new StringBuilder();
            sb.Append("\n## ").Append(from.ToString("HH:mm")).Append("–").Append(to.ToString("HH:mm")).Append("\n\n");
            if (summary != null)
                sb.Append(summary).Append("\n");
            else
                sb.Append("> ⚠ 生成失败：").Append(error ?? "未知原因").Append("\n");
            sb.Append("\n> 数据：切换 ").Append(s.Switches).Append(" 次");
            var list = new List<KeyValuePair<string, double>>(s.SecByProc);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int shown = 0;
            foreach (var kv in list)
            {
                if (shown >= 4) { sb.Append(" 等"); break; }
                sb.Append(shown == 0 ? "；" : "、").Append(StubSpeaker.Human(kv.Key)).Append(" ")
                  .Append((kv.Value / 60.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" 分钟");
                shown++;
            }
            sb.Append("。\n");
            return sb.ToString();
        }

        /// <summary>落盘（追加）。文件不存在时先写头部。返回 null ＝ 成功。</summary>
        public static string AppendToFile(string path, string section, DateTime day)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path))
                {
                    var head = new StringBuilder();
                    head.Append("# 她写的 · ").Append(day.ToString("yyyy-MM-dd")).Append("\n\n")
                        .Append("> 阿助按小时写的使用小结。她的内部状态不在这里 —— 那份在 %LOCALAPPDATA%\\AzhuPet\\。\n");
                    File.WriteAllText(path, head.ToString(), new UTF8Encoding(false));
                }
                File.AppendAllText(path, section, new UTF8Encoding(false));
                return null;
            }
            catch (Exception ex) { return "写入失败：" + ex.Message; }
        }

        /// <summary>
        /// 编排：读记忆 → 聚合 → 调模型 → 落盘。模型失败**不静默**（失败原因写进文件）。
        /// 用户拍板「必须用真模型」—— 没有模板降级：模板编不出「他做了什么」。
        /// </summary>
        public static async Task<string> RunAsync(string vaultPath, List<Dictionary<string, JsonElement>> rows,
                                                  DateTime from, DateTime to)
        {
            Stats s = Aggregate(rows, from, to);
            string section;
            if (s.Lines == 0)
            {
                section = BuildSection(null, "这一段没有记录（她没在运行，或没观察到切换）", s, from, to);
            }
            else
            {
                var msgs = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new object[] { new Dictionary<string, object>
                            { ["type"] = "text", ["text"] = BuildPrompt(s, from, to) } },
                    },
                };
                var (ok, text) = await TraeChat.ChatAsync(msgs);
                string summary = null, error = null;
                if (!ok) error = "模型没回：" + One(text);
                else
                {
                    summary = (text ?? "").Trim();
                    if (summary.Length == 0) { summary = null; error = "模型回了空内容"; }
                    else if (summary.Length > 400) { summary = null; error = "超 400 字（丢弃）"; }
                }
                section = BuildSection(summary, error, s, from, to);
            }
            string err = AppendToFile(FilePath(vaultPath, to), section, to);
            return err;      // null ＝ 落盘成功
        }

        private static string One(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}

// 记忆层（P0）—— 一行一条，JSONL 追加。
//
// ⚠ 落点必须在 %LOCALAPPDATA%\AzhuPet\，**不能进 Obsidian 库**：
//   `40 Projects/` 在坚果云同步范围内，她的内部状态（计数、去重表、这份记忆）
//   一旦进库就会上云、落到手机。同族的旧教训：借登录态凭据曾差点写进同步目录。
//   她「想让你看见」的产出走另一个落点（`40 Projects/阿助（桌宠）/她写的/`），两者刻意分开。
//
// ⚠ 为什么用 JSONL 而不是一个 JSON 数组：追加是原子的、不会因为半路崩掉而毁掉全部记忆。
//   一个 JSON 数组每次都要整体重写 —— 崩在写入中间就什么都没了。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class Memory
    {
        public static string Dir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet");
        }

        /// <summary>测试用：把记忆写到别处。⚠ 自检**绝不污染真记忆** —— 否则每跑一次判据，
        /// 她的记忆里就多几条假记录，而判据本身会把它们当成「她说过的话」。</summary>
        public static string OverridePath;

        public static string FilePath()
        {
            return string.IsNullOrEmpty(OverridePath) ? Path.Combine(Dir(), "memory.jsonl") : OverridePath;
        }

        /// <summary>记下这次她看到了什么、说没说话、说了什么。失败**不许静默**。</summary>
        public static string Append(Observation obs, Verdict v, string speaker, DateTime now)
        {
            var row = new Dictionary<string, object>
            {
                ["at"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["proc"] = obs == null ? null : obs.Process,
                ["title"] = obs == null ? null : obs.Title,
                ["prevDwell"] = obs == null ? 0 : Math.Round(obs.PrevDwell, 1),
                ["speaker"] = speaker,
                ["spoke"] = v != null && v.Speak,
                ["said"] = v == null ? null : v.Text,
                ["why"] = v == null ? null : v.Why,
                // 观察种类（null=换应用 / roast-*）。⚠ 小时总结的**时长统计按它排除 roast 行**：
                //   roast 行的 prevDwell 是「当前应用的累计停留」，不是一次切换 ——
                //   不排除的话同一个应用的时长会被重复计入。
                ["reason"] = obs == null ? null : obs.Reason,
                // 手动触发的行（prevDwell 恒 0、proc 来自 ReadTarget 口径）—— 它**不是**一次切换，
                //   小时总结的归属链必须跳过它，否则下一行的时长会被归到错误的应用头上。
                ["manual"] = obs != null && obs.Manual,
            };

            string line;
            try { line = JsonSerializer.Serialize(row); }
            catch (Exception ex) { return "序列化失败：" + ex.Message; }

            try
            {
                Directory.CreateDirectory(Dir());
                File.AppendAllText(FilePath(), line + "\n", new UTF8Encoding(false));
                return null;   // 无错
            }
            catch (Exception ex) { return "写入失败：" + ex.Message; }
        }

        /// <summary>
        /// 读回全部行 + **坏行计数**。
        /// ⚠ 坏行必须单独计数 —— 否则「解析跳过一行」会把自己渲染成「她少说了一句」，
        ///   而那是两件完全不同的事（项目老毛病：静默跳过把「文件没找到」变成「少一行」）。
        /// </summary>
        public static List<Dictionary<string, JsonElement>> ReadAll(out int badLines, out string error)
        {
            badLines = 0; error = null;
            var rows = new List<Dictionary<string, JsonElement>>();
            string f = FilePath();
            if (!File.Exists(f)) { error = "还没有记忆文件：" + f; return rows; }

            string[] lines;
            try { lines = File.ReadAllLines(f, Encoding.UTF8); }
            catch (Exception ex) { error = "读取失败：" + ex.Message; return rows; }

            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) { badLines++; continue; }
                try
                {
                    using (var doc = JsonDocument.Parse(t))
                    {
                        var d = new Dictionary<string, JsonElement>();
                        // ⚠ 必须 Clone：JsonElement 只是 JsonDocument 内存上的**视图**，
                        //   doc 一 Dispose 再访问就抛 ObjectDisposedException —— 而且是在「用到它」时才炸，
                        //   不是在这里炸。不 Clone 的话这段读回来看着是全绿的，下一环才红。
                        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
                        rows.Add(d);
                    }
                }
                catch { badLines++; }
            }
            return rows;
        }

        /// <summary>总行数（含坏行）——判据的第一条是「读得到总数」，读不到就不许报成功。</summary>
        public static int LineCount()
        {
            string f = FilePath();
            if (!File.Exists(f)) return -1;
            try
            {
                int n = 0;
                using (var sr = new StreamReader(f, Encoding.UTF8))
                    while (sr.ReadLine() != null) n++;
                return n;
            }
            catch { return -1; }
        }
    }
}

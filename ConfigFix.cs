// 一次性修复工具：把被「转义不对称」写胖的 config.json 读回来、正常写回去。
// 用法：pet.exe --fixconfig
// 安全性：原文件先备份成 config.json.bak_fix_<时间戳>，再原地重写。
//
// ⚠⚠ 2026-09-20 第一次实现**失败了**，值得记下来：它走 Load() → Save()，
//   而 Load() 会把文件里那一大坨 `\\` 按 JSON 规则**保真读回**成约 1.34 亿个 `\` 字符，
//   Save() 再转义成约 2.68 亿字符写回 ⇒ **字节数原地不动**（268435986 → 268435986），
//   退出码 1，看着像「工具坏了」。
//   真问题不是工具坏了，是**策略错了**：那个值已经不可逆还原（多出来的反斜杠把
//   `\Obsidian_SecondBrain\` 那一段吃掉了），**保真**等于**保留了垃圾**。
//   ⇒ 正解是判定为垃圾、重置默认值。这条判断现在落在 PetConfig.IsPoisonedPath() 里，
//     Load() 也走同一条（面板打开即自愈）。本工具只是「立刻缩文件 + 留备份」的那一步。
using System;
using System.IO;

namespace AzhuPet
{
    internal static class ConfigFix
    {
        public static int Run(Cli o)
        {
            string path = Path.Combine(PetConfig.Dir, "config.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("没有配置文件，无需修复：" + path);
                return 0;
            }
            long before = new FileInfo(path).Length;

            // 先读（Load 里已含自愈），看它到底是不是被写坏了 —— 修之前先确认病灶，
            // 而不是「先备份再无条件重写」。没坏就别动用户的文件。
            var c = PetConfig.Load();
            bool poisoned = IsRawValuePoisoned(path);
            if (!poisoned)
            {
                Console.WriteLine("配置文件没有被写坏（" + before + " B），无需修复。");
                return 0;
            }

            // 备份（带时间戳，不覆盖任何已有东西）
            string bak = path + ".bak_fix_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(path, bak, false);

            c.Save();

            long after = new FileInfo(path).Length;
            Console.WriteLine("病灶   = vaultPath 的反斜杠膨胀（已不可逆还原，重置为默认）");
            Console.WriteLine("修复前 = " + before + " B");
            Console.WriteLine("修复后 = " + after + " B");
            Console.WriteLine("备份   = " + bak);
            Console.WriteLine("vaultPath = " + c.VaultPath);
            Console.WriteLine(after < before ? "✅ 已修复" : "⚠ 没有变小，请检查");
            return after < before ? 0 : 1;
        }

        /// <summary>直接看文件里的 vaultPath 原文是不是膨胀值。
        /// ⚠ 不能只看 Load() 之后的值：Load 已经把它自愈成默认值了，
        ///   那时候再判断永远是「没坏」。**判据要扫输入，不能扫处理结果。**
        ///   （同族旧坑：扫处理结果 ⇒ 处理失败退回兜底时判据检查官方副本、永远全绿。）</summary>
        private static bool IsRawValuePoisoned(string path)
        {
            try
            {
                string s = File.ReadAllText(path, System.Text.Encoding.UTF8);
                int i = s.IndexOf("\"vaultPath\"", StringComparison.Ordinal);
                if (i < 0) return false;
                int q = s.IndexOf('"', s.IndexOf(':', i) + 1);
                if (q < 0) return false;
                // 从值的起始引号往后数反斜杠，数到 65 个就足够判定（省得扫 2.68 亿字符）
                int bs = 0;
                for (int k = q + 1; k < s.Length; k++)
                {
                    if (s[k] == '"') break;
                    if (s[k] == '\\' && ++bs > 64) return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}

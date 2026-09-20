// 已知第三方余额源模板安装器：只写非敏感结构，绝不携带 token/cookie。
using System;
using System.Linq;

namespace AzhuPet
{
    internal static class BalanceTemplateInstaller
    {
        public static int Run(string name)
        {
            if (!string.Equals(name, "sui-xiang", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("未知余额模板：" + name);
                return 2;
            }

            var list = BalanceSources.Load();
            var source = list.FirstOrDefault(x =>
                string.Equals(x.Name, "随想余额", StringComparison.OrdinalIgnoreCase)
                || (x.Url ?? "").IndexOf("sui-xiang.net/api/v1/auth/me", StringComparison.OrdinalIgnoreCase) >= 0);
            if (source == null)
            {
                source = new BalanceSource();
                list.Add(source);
            }

            source.Name = "随想余额";
            source.Url = "https://www.sui-xiang.net/api/v1/auth/me?timezone=Etc%2FGMT-8";
            source.Method = "GET";
            source.PathExpr = "data.balance";
            source.Unit = ""; // 响应不含币种；未核实前不猜“元/美元”
            source.SecretFile = "sui-xiang.secret.txt";
            source.HeadersText = "accept: application/json";
            source.Body = "";
            source.Enabled = false; // 必须由用户粘贴一枚新 Token 并测试后再启用

            BalanceSources.Save(list, BalanceSources.ConfigPath());
            Console.WriteLine("已安装随想余额模板（默认停用）：" + BalanceSources.ConfigPath());
            Console.WriteLine("字段路径：data.balance；凭据文件：sui-xiang.secret.txt");
            return 0;
        }
    }
}

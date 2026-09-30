# trae-ext —— 随桌宠分发的 Trae 扩展

这个目录是**桌宠安装包的一部分**：桌宠在「余额配置 → 安装同步扩展」时把
`extension.js` + `package.json` 拷进 Trae 的扩展目录，此后每次 Trae 启动都会把
当前会话令牌写回阿助的凭据文件，用户不必再手工粘贴。

## 名字与落点（三处必须同名，改名要一起改）

| 位置 | 写的什么 |
|---|---|
| `TraeExtInstaller.SourceDirName` | `trae-ext`（桌宠去哪找源文件） |
| `pet.csproj` 的 `Content Include` | 把本目录拷进构建输出 |
| `pack-release.cmd` / `installer/azhupet.iss` | 把本目录打进 zip 与安装包 |

落点规则：`%USERPROFILE%\<product.json 的 dataFolderName>\extensions\`
（本机 = `~/.trae-cn/extensions/`）。桌宠侧自适应解析，不硬编码。

## ⚠ 两个踩过的坑，别再踩

1. **`engines.vscode` 不能写 `"*"`。**
   Trae 的 `sharedprocess.log` 原文：`"engines.vscode" (*) 中指定的版本不够具体`。
   写了 `*` 的扩展会被登记进 profile、**但永远不会激活**，症状是扩展目录里文件明明在、
   却一行日志都没有。内置扩展（`extensions/solo-lite`）写 `*` 是**内置专属豁免**，
   别拿它当范例。`--traeexttest` 有一条断言专门盯这个。
2. **`registerCommand` 遇重名会 throw。** 它排在 `activate` 靠前，裸调会把整个
   `activate` 打断（业务逻辑一行都跑不到），必须包 `try/catch`。

## 只动一行

扩展**只**改 `authorization:` 那一行：

- 已有该行 → 换值（行数不变、其它行逐字不变、行尾不变）
- 没有该行但有 `---headers---` → 紧随其后插一行
- 都没有 → 插在 URL 行之后；连 URL 都没有 ⇒ 拒绝（不猜）

**文件不存在时它不会创建**：骨架里的 URL 与 body 是桌宠的事实，扩展凭空造一份
等于给同一份数据加第二个口径。那份骨架由桌宠在「安装同步扩展」时按需写出。

## 不联网、不外发

它只做一件事：向本进程内的 Trae 命令服务要一份令牌，然后写一个本地文件。
不抓包、不读内存、不发请求、不伪造签名头；令牌本身从不落日志（只记长度与 iat/exp）。

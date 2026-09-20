# 阿助 · 桌宠（AzhuPet）

一只会说人话的 Windows 桌宠。她不只会眨眼——她会**观察**你换了哪个窗口、**读**你屏幕上的字（可选）、**吐槽**你正在看的内容，还会**每小时写一篇**使用小结。

纯 WPF 实现（无第三方游戏引擎），代码里带着 9 套离线判据与负对照——这个项目的每一步都有"怎么验"的答案。

> 个人项目，仍在快速迭代中。Issues / PR 欢迎。

## 功能一览

- **桌面宠物**：WPF 3D 渲染、拖拽、打盹、跳跃、日夜调光、全屏自动隐退
- **自发说话**：换应用时评一句；同一应用里窗口标题变化时吐槽；长时间安静则冒一句保底
- **读屏（可选，默认关）**：Windows 自带 OCR 读屏幕文字，把内容接进她的话里——全程本机，文字不出你的电脑
- **每小时小结**：真模型替你写「这一小时做了什么」，落成 Markdown 日记
- **余额显示**：可选，支持多来源（详见下文）
- **人格**：她有名字（阿助）、有称呼你的方式、有说话的边界——人格文本独立成文件，改完即生效

## 系统要求

- Windows 10 19041 或更高（离线 OCR 依赖系统组件）
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)（从源码运行时需要）

## 快速开始

```bash
# 方式一：源码运行（需要 .NET 9 SDK）
git clone https://github.com/mauyumaster/azhu-pet.git
cd azhu-pet
run-pet.cmd        # 或：dotnet run -c Release
```

```bash
# 方式二：发布产物（zip，需 .NET 9 Desktop Runtime）
# 从 GitHub Releases 下载，解压后双击 pet.exe
```

首次启动：托盘出现图标。**不开模型也能玩**——默认台词是免费模板句；想要「会说人话的她」，按下文配一条模型通道。

## 台词模型：三种通道

| 通道 | 门槛 | 适合谁 |
| --- | --- | --- |
| **模板台词**（默认） | 零配置、零花费、逐字节可预测 | 先体验；也用作判据的对照组 |
| **OpenAI 兼容端点**（推荐） | 任意 OpenAI 兼容服务的 key | 大多数人：DeepSeek / 硅基流动 / OpenRouter / 本地 ollama 都行 |
| **Trae 通道** | 需已安装并登录 Trae，凭据从客户端抓取 | 进阶；非官方接口，随时可能失效 |

**配置入口只有一个**：托盘右键 →「设置…」。说话方式、模型通道、读屏与隐私、每小时小结、外观与启动，全在那一个窗里。

**配置 OpenAI 兼容通道**：设置面板的「模型通道」分区填三项（接口地址 / 模型名 / API key）。例如 DeepSeek：`https://api.deepseek.com` ＋ `deepseek-chat` ＋ 你的 `sk-…`。本地 ollama：`http://localhost:11434` ＋ `qwen2.5` ＋ key 留 `ollama`。两项都填才走它，否则回落 Trae 通道。

命令行等价：

```bash
pet.exe --openai-base https://api.deepseek.com --openai-model deepseek-chat --openai-key sk-xxx
```

**读屏吐槽**要她「看得见」，需在设置里同时勾选：「台词用模型生成」＋「把读到的字放进提示」。只勾后者零效果（模板说话人不读屏幕文字）。

## 隐私（先说清楚她往外发什么）

- **自动发言只发应用名**（如 `msedge`→「浏览器」），**从不发送窗口标题**
- **屏幕文字**默认关闭；打开后，识别出的文字会进入模型提示——这是唯一会带内容出本机的通道，托盘菜单可随时关
- **每小时小结**只用「应用 → 时长」统计，不含标题、不含屏幕文字
- 凭据与数据全部落在 `%LOCALAPPDATA%\AzhuPet\`（不在同步目录、不进仓库）；构建脚本会在输出目录出现凭据文件时**直接报错**

## 余额显示（可选）

托盘「余额配置…」支持多来源查询（Trae / WorkBuddy / 自定义接口 / DeepSeek）。凭据只存本机。不配置则隐藏该行，不影响任何功能。

## 自检（这个项目不一样的部分）

```bash
pet.exe --watchtest      # 感知状态机 35 项
pet.exe --speaktest      # 表达链路 43 项
pet.exe --summarytest    # 小时总结 14 项
pet.exe --ocrtest        # OCR 隐私门 50 项
pet.exe --fstest         # 全屏判定 10 项
pet.exe --personatest    # 人格注入 11 项
pet.exe --shellprobe     # 真机窗口真值探针
```

每套都带负对照（`--no-xxx`）：关掉被测机制，判据必须红——判据没被逼红过，它的绿就没有信息量。

## 构建 / 发布

```bash
dotnet build -c Release
# 发布单文件产物（需用户自装 .NET 9 Desktop Runtime）：
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

## License

- **代码**：[MIT](LICENSE)
- **人格文本**（`persona.md`，她的自我认知与说话方式）：[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh) —— 非商业使用、署名、相同方式共享。这是「她是谁」的一部分，请像对待角色设定一样对待它。

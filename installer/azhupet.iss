; ==============================================================================
;  阿助桌宠 · 安装程序脚本（Inno Setup 6）
; ==============================================================================
;  编译：
;     ISCC.exe azhupet.iss /DAppVersion=0.1.1
;  或由 pack-release.cmd 在 publish 之后自动调用（见该脚本尾注）。
;
;  ⚠ 本文件必须存成 **UTF-8 with BOM** —— ISCC 按 ANSI 解码无 BOM 的 .iss，
;     中文注释会变乱码甚至直接编译报错。这跟项目里「.cmd 必须 CRLF + ASCII」
;     是同一类纪律：编码/行尾不是风格问题，是可执行性的一部分。
;
;  ⚠ 输入不是源码，而是 publish 产物（三文件）。先跑 pack-release.cmd。
;
;  ── 用户能看到的三个选择，全部由 [Tasks] 的复选框驱动 ──
;     1. 安装位置   → Inno 自带的「选择目标位置」页（DisableDirPage=no）
;     2. 桌面图标   → Tasks: desktopicon
;     3. 开机自启   → Tasks: autostart（写 HKCU 的 Run 键）
; ==============================================================================

; ---- 版本号：命令行传入。唯一真值仍是 pet.csproj 的 <Version> ----
; 不在本文件硬编码版本 —— 本项目踩过「同一份数据两个落点」的坑。
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName "阿助桌宠"
#define AppExe "pet.exe"
#define AppPublisher "mauyumaster"
#define AppURL "https://github.com/mauyumaster/pet"

; publish 产物目录（与 pack-release.cmd / deliver.cmd 里的 PUBDIR 必须一致）
; ⚠ 换 TFM 时这里也要改 —— csproj 里有同样的警告，两边不同步 =
;   安装器打出一个空壳包，而每一步都报成功。
#define PubDir "..\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"

; ⚠⚠ 编译期一致性闸：安装包**标称的版本**必须等于里面那个 exe 的**真实版本**。
;
; 2026-09-23 实测踩到：publish 目录里躺着 0.1.0 的旧构建（上一轮打 v0.1.1 时
; 为了绕开被锁的 exe 用了 -o 到独立目录，就没刷新这里），而安装包用
; /DAppVersion=0.1.1 编译 ⇒ **装出来标 0.1.1、一跑报 0.1.0**。
; 安装程序自己完全不会发现这件事 —— 它只知道自己的版本号，不知道肚子里装的是什么。
; 这与项目里「同一个数据两个落点」是同一类病：版本号只能有一处真值，
; 这里就必须有办法**证明**那一处跟安装包一致。
;
; FileVersion 形如 "0.1.1.0"（.NET 生成的四段），所以拿 "<AppVersion>." 做前缀比。
; 用前缀而不是 Contains —— "0.1.1." 不能匹配上 "0.1.10.0"。
#if !FileExists(PubDir + '\pet.exe')
  #error MISSING_PET_EXE: PubDir 里没有 pet.exe —— 先跑 pack-release.cmd（或 dotnet publish）再编译本脚本。
#endif

; ⚠ 用 GetVersionNumbersString —— Inno 6.7 起 GetFileVersion 已改名，旧名会报 warning。
#define RealFileVer GetVersionNumbersString(PubDir + '\pet.exe')
#pragma message "  pet.exe FileVersion = " + RealFileVer + "   AppVersion = " + AppVersion
#if Pos(AppVersion + '.', RealFileVer) != 1
  #error EXE_VERSION_MISMATCH: PubDir 里的 pet.exe 版本不是 AppVersion（见上面两行）。先跑 pack-release.cmd 刷新构建，或检查 /DAppVersion 传对没有。
#endif

; ⚠ 官方 Inno 6 发行版**不含**简体中文语言文件（Languages 目录里没有
;    ChineseSimplified.isl）。装上第三方翻译后取消本行注释即可。
;#define HaveChinese

; 安装前检测 .NET 桌面运行时，缺失则引导到官方下载页。
; ⚠ 这条只在「框架依赖」路线下需要（本项目当前路线：publish 用
;   --self-contained false ⇒ 目标机必须有对应运行时）。
;   若将来改出自包含版，把这行注释掉即可。
#define NeedDotNet
#define DotNetMajor 9


[Setup]
; AppId 是升级识别的关键：同一个 AppId 才会被认成「同一软件的新版本」。
; 用字符串形式（官方推荐 GUID，两者都合法）。**定下来就不要改**。
AppId=AzhuPet-DesktopPet
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}

; ⚠⚠ 权限模式 —— 这条决定了自更新还能不能用，不是审美问题。
;   lowest = 每用户安装（默认落 {localappdata}\Programs\AzhuPet）：
;            **不弹 UAC**，而且她的自更新写得住（见下）。
;   admin  = 全体用户安装（{autopf} = {pf}\AzhuPet）：更「正规」，
;            但自更新会**永久失效** —— Updater.ApplyPending 是原地替换
;            Environment.ProcessPath 同目录的文件（让位 .old -> 就位），
;            Program Files 写不进去。失败形态极隐蔽：提权不会在后台静默
;            发生，结果是「更新走完了、版本号没变、一个错都不报」。
;   ⇒ 默认 lowest；命令行 /ALLUSERS 可由用户显式覆盖（风险自负）。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog

; {autopf} 随 PrivilegesRequired 变化：lowest -> {localappdata}\Programs，
; admin -> {pf}。同一行两种模式各得其所。
DefaultDirName={autopf}\AzhuPet
DisableDirPage=no
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes

; 许可页：代码 MIT + persona CC BY-NC-SA 的合并说明（本目录下）。
; ⚠ 该文件必须与 .iss 同编码（UTF-8 BOM），否则中文在向导里是乱码。
LicenseFile=LICENSE-INSTALLER.txt

OutputDir=..\release
OutputBaseFilename=AzhuPet-v{#AppVersion}-win-x64-setup
SetupIconFile=..\pet.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 她在跑时安装/卸载会撞上被锁的 pet.exe。CloseApplications 让 Inno 自己
; 检测并提示用户关掉 —— 正好顶上 deliver.cmd 里那段手工 tasklist 守卫。
CloseApplications=yes
RestartApplications=no


[Languages]
; 默认英文向导（官方发行版一定带 Default.isl）。
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifdef HaveChinese
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif


[Tasks]
; ★ 这两个复选框就是「是否给桌面添加图标 / 是否开机自启」。
; Flags 控制默认勾选状态：
;   checkedonce = 首次安装默认勾上，之后记住用户上次的选择
;   unchecked   = 默认不勾（改系统行为的事，默认不勾更礼貌）
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "autostart"; Description: "开机时自动启动阿助（之后可在设置里改）"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked


[Files]
; 四文件，与 pack-release.cmd 打 zip 的内容**必须一致**。
; ⚠ 逐条列出而不用 *.* 通配：缺文件时 Inno 在**编译期**就报错，而不是打出一个
;   「装完能开、但没有模型/不是她」的包 —— 那种包每一处检查都是绿的。
Source: "{#PubDir}\{#AppExe}";                DestDir: "{app}";       Flags: ignoreversion
Source: "{#PubDir}\persona.md";               DestDir: "{app}";       Flags: ignoreversion
Source: "{#PubDir}\model\chibi_maid_pet.glb"; DestDir: "{app}\model"; Flags: ignoreversion
; ⚠ WebView2Loader.dll 必须一起装（2026-09-25 加）—— 它**没有**被打进单文件 exe，
;   缺了它，「浏览器登录 / 自动取余额」会在干净机器上直接 DllNotFoundException。
;   开发机上一直没暴露，是因为本机 PATH 上恰好有一份（Windows Performance Toolkit 自带的）。
;   实测方式：把 PATH 收窄到 System32，再跑 pet.exe --webtest。
Source: "{#PubDir}\WebView2Loader.dll";       DestDir: "{app}";       Flags: ignoreversion


[Icons]
; ⚠⚠ WorkingDir 必须写。Config.cs 的 ResolveModel 与 Persona.Resolve 都会看
;    **当前工作目录**（new[]{ BaseDirectory, Directory.GetCurrentDirectory() }）。
;    不写工作目录时，快捷方式启动的 cwd 可能是 C:\Windows\System32，
;    结果是「能跑、能说话、但不是她」（persona 悄悄回退到内置骨架）——
;    最难查的那种故障：所有自检都是绿的。
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Comment: "启动 {#AppName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon


[Registry]
; 开机自启：写 HKCU Run 键，值格式与 PetConfig.SetAutostart 完全一致
;   （"\"" + exePath + "\"" —— 即带引号的全路径）。
; ⚠ 格式一致很重要：她从安装目录启动时，HealAutostartIfOn ->
;   AutostartHealTarget 会看到 currentValue == want，返回 null ⇒ **不动作**。
;   格式不一致就会被她「自愈」成另一个值，而用户完全看不见这件事。
; uninsdeletevalue：卸载时自动删掉这个值，不留指向不存在文件的死项。
; 用 HKCU 而非 HKLM —— 无需管理员，且每用户独立，与 lowest 权限模式配套。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "AzhuPet"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart


[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent


[UninstallDelete]
; 自更新会留下 .old 让位文件（Updater.ApplyPending 的产物），Inno 不认识它们。
; ⚠ 绝不在这里删 %LOCALAPPDATA%\AzhuPet —— 那是 config、记忆和 API key，
;   卸载重装不该丢记忆。要不要连个人数据一起删，在 [Code] 里单独问。
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\persona.md.old"
Type: files; Name: "{app}\model\chibi_maid_pet.glb.old"


[Code]
// ⚠⚠ 本段一律用 // 注释，不用 {} 注释。
//    Inno 的 Pascal 注释 { ... } **不支持嵌套**：注释里一旦出现
//    {app} 这样的常量写法，其中的 } 会提前结束注释，把后半句当成代码
//    去编译。这是个只在写注释时才会踩的坑。

// 卸载完成后问一句是否连个人数据一起删 —— 正规软件的惯例，
// 也避免把用户的记忆误删掉。默认选「否」。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('是否同时删除阿助的个人数据（记忆、设置、API key）？' + #13#10 + #13#10 +
              '选择「否」则保留在本地，重新安装后她仍然记得你。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\AzhuPet'), True, True, True);
end;

#ifdef NeedDotNet
const
  DotNetDesktopFxKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

// .NET 桌面运行时检测 —— **注册表和文件系统两处都查**。
//
// ⚠⚠ 第一版只查注册表，被实测推翻：
//   2026-09-23 在本机查 HKLM 的这个 sharedfx 键，**整条路径都不存在**，
//   而 %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App 下面明明有
//   8.0.30 / 8.0.31 / 9.0.6 / 10.0.11 / 10.0.12 五个运行时。
//   ⇒ 这个键只在**部分安装来源**（官方安装包）下才写；Visual Studio 和
//     dotnet-install 脚本装的都不写它。
//   ⇒ 只查注册表的话，**已经装了运行时的人也会被拦下** —— 这是所有误判方向里
//     最糟的一个：他明明装了，却被告知没装。
//   ⚠ 注意这条是被「先验证前提、再相信逻辑」抓出来的：函数编译通过、
//     逻辑自洽，错的只是「注册表是权威」这个前提。
function HasDotNetDesktop(Major: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  R: Integer;
  Prefix: String;
  Dir: String;
  FindRec: TFindRec;
  Roots: array[0..2] of String;
begin
  Result := False;
  Prefix := IntToStr(Major) + '.';

  // 路径 A：注册表（官方记录，但并非所有安装来源都写）
  if RegGetValueNames(HKLM, DotNetDesktopFxKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Prefix, Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;

  // 路径 B：文件系统 —— 机器级与用户级两个常见落点
  //   {commonpf}    官方安装包 / VS 的机器级安装
  //   {localappdata} dotnet-install.ps1 的默认用户级安装
  Roots[0] := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Roots[1] := ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
  for R := 0 to 1 do
  begin
    Dir := Roots[R];
    if not DirExists(Dir) then Continue;
    if FindFirst(Dir + '\*', FindRec) then
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0)
           and (Pos(Prefix, FindRec.Name) = 1) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Rc: Integer;
  Found: Boolean;
begin
  Result := True;
  Found := HasDotNetDesktop({#DotNetMajor});
  // 写进安装日志（配 /LOG 用），让「检测到底认没认出来」可查而不是靠猜。
  Log('dotnet desktop ' + IntToStr({#DotNetMajor}) + ' detected: ' + IntToStr(Ord(Found)));
  if Found then Exit;

  // ⚠ 这里**不硬阻止**，只问一句。理由：检测本身有不确定性（安装来源太多），
  //   硬阻止会把「其实装了、只是没检测到」的人挡在门外 —— 比漏装更糟，因为他
  //   会完全摸不着头脑。默认按钮选「是」（继续），让误判的代价降到最小。
  if MsgBox('未检测到 .NET ' + IntToStr({#DotNetMajor}) + ' 桌面运行时。' + #13#10 + #13#10 +
            '阿助需要它才能启动。可能的原因：' + #13#10 +
            '  · 确实还没装 —— 选「否」打开官方下载页' + #13#10 +
            '（装好后重新运行本安装程序）' + #13#10 +
            '  · 装在了非常规位置 —— 选「是」直接继续' + #13#10 + #13#10 +
            '要继续安装吗？',
            mbConfirmation, MB_YESNO) = IDYES then
    Exit;

  ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/{#DotNetMajor}.0',
            '', '', SW_SHOWNORMAL, ewNoWait, Rc);
  Result := False;
end;
#endif

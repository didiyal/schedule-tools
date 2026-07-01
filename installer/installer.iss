; ── 待办计时器 安装器 (Inno Setup 6) ──────────────────────────────
; 编译: ISCC.exe installer.iss   (build_installer.bat 会自动调用)
;
; 设计要点:
;   * 单用户免管理员: 默认装 {localappdata}\Programs\待办计时器, 向导里可改路径
;   * 固定 AppId: 重复运行即检测到旧版 → 原地覆盖升级(同目录)
;   * 升级前 taskkill 关闭运行中的托盘程序, 释放 EXE 占用
;   * data/ 故意不打包: 由程序首次运行自建 → 升级时用户数据自动保留
;   * 卸载时弹窗: 可选是否一并删除待办数据

#define MyAppName      "待办计时器"
#define MyAppVersion   "1.1.0"
#define MyAppExeName   "待办计时器.exe"
#define MyAppSourceExe "..\待办计时器.exe"

[Setup]
; AppId 固定不变(仅此处版本号会随发布改) —— 升级检测的唯一标识
AppId={{39CAAA24-7E5E-4750-AC6A-E134C7BA9E83}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
UninstallDisplayName={#MyAppName}

; 单用户安装(免 UAC), 默认装到当前用户的 LocalAppData
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousPrivileges=no
UsePreviousAppDir=yes

; 输出到上级目录(tools/), 文件名带版本号
OutputDir=..
OutputBaseFilename=待办计时器_Setup_{#MyAppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; 升级时自动关闭占用 EXE 的旧进程(配合下方 taskkill 双保险)
CloseApplications=force
RestartApplications=no

[Languages]
; 简中语言文件本地随包(kira-96 译, 适用 6.5.0+, 已加 UTF-8 BOM)。
; Inno 官方安装包不含中文 → 不能用 compiler: 前缀, 改为引用本地 ChineseSimplified.isl。
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式(&D)"; GroupDescription: "附加选项:"; Flags: unchecked

[Files]
; 主程序: 强制覆盖(忽略版本号)。data/ 不在此列 → 升级时保留用户数据
Source: "{#MyAppSourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // 升级前关闭运行中的旧版本(托盘常驻), 否则 EXE 被占用无法覆盖。
  // 找不到进程时 taskkill 返回非 0, 属正常, 不影响安装。
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM "{#MyAppExeName}" /F',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // 询问是否一并删除待办数据。默认「否」—— 保留 data/ 以便将来恢复或重装后继续使用。
    if MsgBox('是否同时删除本机的待办数据?' #13#10 '选「否」将保留 data 目录, 便于将来恢复或重装后继续使用。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{app}\data'), True, True, True);
    end;
  end;
end;

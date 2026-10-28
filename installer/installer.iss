; ── 待办计时器 安装器 (Inno Setup 6) ──────────────────────────────
; 编译: ISCC.exe installer.iss   (build_installer.bat 会自动调用)
;
; 设计要点 (v1.4.0 WPF 版, 2026-10-27):
;   * 单用户免管理员: 默认装 {localappdata}\Programs\待办计时器, 向导里可改路径
;   * 固定 AppId: 与 1.2 Python 版一致 → 原地升级, data/ 自动继承
;   * 源= WPF self-contained 发布目录 (installer/publish/), 用户机器零依赖
;   * 旧 EXE 名 待办计时器.exe (Python 1.2) 安装时清理, 防误启动
;   * data/ 故意不打包: 由程序首次运行自建 → 升级时用户数据自动保留
;   * 升级前 taskkill 新旧两个 EXE 名 (托盘常驻占用), 卸载时可选保留数据

#define MyAppName      "待办计时器"
#define MyAppVersion   "1.4.5"
#define MyAppExeName   "TodoTimer.exe"
#define MyAppLegacyExe "待办计时器.exe"

[Setup]
AppId={{39CAAA24-7E5E-4750-AC6A-E134C7BA9E83}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
UninstallDisplayName={#MyAppName}

DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousPrivileges=no
UsePreviousAppDir=yes

OutputDir=..
OutputBaseFilename=待办计时器_Setup_{#MyAppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

CloseApplications=force
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式(&D)"; GroupDescription: "附加选项:"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{app}\{#MyAppLegacyExe}"

[Registry]
; 应用身份注册（HKCU 免管理员）：
; 1) App Paths —— Win+R 直接输 TodoTimer、Windows 搜索可索引
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey
; 2) AUMID 应用身份 —— 通知中心 toast 显示名称/图标、搜索与任务栏正确归组（与 App 内 SetAumid 对应）
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\TodoTimer.App"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\TodoTimer.App"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\app.ico"; Flags: uninsdeletekey

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
; AUMID 注册引用的图标
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

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
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM "{#MyAppExeName}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM "{#MyAppLegacyExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if SuppressibleMsgBox('是否同时删除本机的待办数据?' #13#10 '选「否」将保留 data 目录, 便于将来恢复或重装后继续使用。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DelTree(ExpandConstant('{app}\data'), True, True, True);
    end;
  end;
end;

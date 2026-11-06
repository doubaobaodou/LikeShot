; LikeShot 安装脚本。由 pack.cmd 调用 ISCC 编译，版本号从命令行传进来：
;   ISCC /DMyAppVersion=0.1.6 installer\LikeShot.iss
; 这个文件必须是 UTF-8 带 BOM，否则向导里的中文会按系统 ANSI 码页解析成乱码。

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
; AppId 是升级与卸载的匹配键，发布后永远不能改，改了会出现「装了两份」。
AppId={{7C4B9E2D-8F31-4A6B-B0D5-5E2A9C7F4D13}
AppName=LikeShot
AppVersion={#MyAppVersion}
AppVerName=LikeShot {#MyAppVersion}
AppPublisher=doubaobaodou
AppContact=https://github.com/doubaobaodou/LikeShot
DefaultDirName={autopf}\LikeShot
DefaultGroupName=LikeShot
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Inno 默认是 auto：检测到之前装过就继承旧路径、把「选择安装位置」这一页跳掉，
; 升级时想换目录就没有入口了。这里强制显示，旧路径仍然会预填好。
DisableDirPage=no
; 只给当前用户装：落到 %LOCALAPPDATA%\Programs\LikeShot，全程不弹 UAC。
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LikeShot-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\LikeShot\app.ico
UninstallDisplayName=LikeShot
UninstallDisplayIcon={app}\LikeShot.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 覆盖安装时如果程序还在跑，提示先退出，别把正在运行的文件写成半份。
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "autostart"; Description: "开机自动启动 LikeShot"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 单文件自包含版：解压后不需要 .NET 运行时。装到固定名字 LikeShot.exe，
; 快捷方式和开机自启指的都是这个不带版本号的路径，升级覆盖后不会指空。
Source: "..\build\singlefile\LikeShot.exe"; DestDir: "{app}"; DestName: "LikeShot.exe"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE"; Flags: ignoreversion

[Icons]
Name: "{group}\LikeShot"; Filename: "{app}\LikeShot.exe"; WorkingDir: "{app}"; Comment: "截图 · 标注 · 长截图 · 钉图"
Name: "{autodesktop}\LikeShot"; Filename: "{app}\LikeShot.exe"; WorkingDir: "{app}"; Comment: "截图 · 标注 · 长截图 · 钉图"; Tasks: desktopicon

[Registry]
; 和程序里托盘那个「开机自启」写的是同一个键同一个值名，
; 两边谁写的都能被对方看见，不会出现「勾了但下次开机没起来」。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "LikeShot"; ValueData: """{app}\LikeShot.exe"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\LikeShot.exe"; Description: "装完立刻运行 LikeShot"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 故意留空：配置、日志、历史图库都在 %APPDATA%\LikeShot，卸载不该碰用户截的图。

[Messages]
; Inno Setup 官方安装包不带简体中文语言文件，向导的按钮和页面标题默认是英文。
; 这里把安装/卸载两条流程上看得见的句子逐条换成中文；名字写错 ISCC 会直接报错，
; 所以这些条目全部对着 Default.isl 抄的，别凭印象增删。
SetupAppTitle=安装
SetupWindowTitle=LikeShot 安装 - %1
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonBack=< 上一步
ButtonNext=下一步 >
ButtonInstall=安装
ButtonFinish=完成
ButtonBrowse=浏览...
ButtonWizardBrowse=浏览...
ButtonNewFolder=新建文件夹
SetupLdrStartupMessage=即将安装 %1。要继续吗？
WelcomeLabel1=欢迎使用 LikeShot 安装向导
WelcomeLabel2=这将把 [name/ver] 安装到你的电脑上。%n%n建议先退出正在运行的 LikeShot（托盘图标右键 → 退出）再继续。
WizardSelectDir=选择安装位置
SelectDirDesc=要把 LikeShot 安装到哪个文件夹？
SelectDirLabel3=安装程序会把 [name] 装到下面这个文件夹。
SelectDirBrowseLabel=点「下一步」继续；想换个位置，点「浏览」。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
WizardSelectTasks=选择附加任务
SelectTasksDesc=要在安装时顺便做这几件事吗？
SelectTasksLabel2=勾需要你需要的项，然后点「下一步」。都不勾也没关系：桌面图标以后随时能补，开机自启在托盘菜单里也能开。
WizardPreparing=准备安装
PreparingDesc=安装程序正在准备安装 [name]。
WizardInstalling=正在安装
InstallingLabel=正在把 [name] 装到你的电脑上，请稍候。
ApplicationsFound=下面这些程序正在占用安装需要更新的文件。建议让安装程序自动关闭它们。
CloseApplications=自动关闭这些程序
DontCloseApplications=不要关闭，我自己来
ErrorCloseApplications=安装程序没能自动关闭全部占用程序。请先在托盘里退出 LikeShot，再重新安装。
SetupAppRunningError=检测到 %1 正在运行。%n%n请先在托盘里退出，然后点「确定」继续，或点「取消」退出安装。
FinishedHeadingLabel=完成 LikeShot 安装向导
FinishedLabel=LikeShot 已经装到你的电脑上了。%n%n可以在桌面或开始菜单里找到它的图标；截图热键是 Ctrl+Alt+A。
ClickFinish=点「完成」关闭安装向导。
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
UninstallDisplayNameMarkCurrentUser=当前用户
ConfirmUninstall=确定要把 %1 及其全部组件从这台电脑上移除吗？%n%n你的设置、日志和历史图库都存在用户数据目录里，卸载不会删除它们。
UninstallStatusLabel=正在从这台电脑上移除 %1，请稍候。
UninstallAppRunningError=检测到 %1 正在运行。%n%n请先在托盘里退出，然后点「确定」继续卸载，或点「取消」退出。
UninstalledAll=%1 已经从你的电脑上移除了。

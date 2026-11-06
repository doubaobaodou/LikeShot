# LikeShot

[![下载最新版本](https://img.shields.io/github/v/release/doubaobaodou/LikeShot)](https://github.com/doubaobaodou/LikeShot/releases/latest)
![构建](https://github.com/doubaobaodou/LikeShot/actions/workflows/build.yml/badge.svg)
![平台](https://img.shields.io/badge/Windows-10%2F11-blue)
[![许可](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Windows 截图工具，界面和操作和 QQ 的截图工具一样。

**热键和 QQ 不冲突**：用的也是 `Ctrl+Alt+A`，QQ 一开就自动把这个键让回给 QQ——
QQ 开着按出来的是 QQ 的截图，QQ 没开按出来的就是这个。QQ 登录时也不会再弹「热键被占用」。
QQ 开着、但你把 QQ 自己的截图热键关掉了：连着按两次之后 LikeShot 就认出「这键没人接」，
之后都由它截图，不用你去设置里改任何东西。

## 下载

去 [Releases](https://github.com/doubaobaodou/LikeShot/releases/latest)，三种包按需要挑：

- `...-setup.exe` —— 正常安装：向导里选目录、勾快捷方式，装完开始菜单和桌面都有图标，
  控制面板里能卸载。只装给当前用户，落在 `%LOCALAPPDATA%\Programs\LikeShot`，全程不弹 UAC
- `...-selfcontained.zip` —— 不想装的人：解压双击 `LikeShot.exe` 就能跑，不用装任何东西
- `...-portable.zip` —— 已经装了 .NET 8 桌面运行时的人，体积小一个数量级

装到 `%LOCALAPPDATA%` 的安装版和绿色版共用同一份 `%APPDATA%\LikeShot`（配置、日志、历史图库），
换着跑不用重新设置；卸载只删程序本身，不会动你截过的图。
没有代码签名，首次运行 SmartScreen 会拦一下，点「仍要运行」。

## 功能

- **截图**：拖拽选区，自动吸附窗口边缘，放大镜显示尺寸和像素颜色
- **标注**：形状（矩形 / 椭圆 / 直线，空心实心可切）、箭头、画笔、马赛克（像素格 / 模糊两档）、
  文字、序号笔；选中工具弹二级工具条——粗细和字号都是连续滑条（字号不再只有小中大三档），
  16 色色板收在颜色下拉里
- **画完还能改**：点一下已画内容就选中（虚线框 + 八个拖角手柄），可以拖走、拖角放大缩小、
  改颜色粗细字号，Delete 删掉；带画具时点到已有内容也是先选中它，要另画一个就点在空白处。
  钉图窗里同样可以，Esc 先取消选中再退出
- **长截图**：纵向 / 横向滚动拼接，可以自动滚动也可以自己滚，滚到底自动收尾
- **钉图**：把截图钉在桌面最上层，滚轮缩放、拖边角改大小，还能继续标注
- **自由截图**：选区状态下右键拖多边形，导出带透明背景
- **历史截图**：每张成品图都自动留一份，设置里的「历史截图」页按时间摆开——
  直接复制到剪贴板、钉回桌面、另存、删掉都行，双击就能丢回截图层接着标注；
  按日期、尺寸、来源搜，点一下选中、Ctrl 多选批删，收藏的置顶且不参与自动清理。
  复制或保存万一没成，图在这儿还找得回来。入口：托盘「历史截图…」或「设置…」→左侧「历史截图」。
- **设置**：托盘菜单「设置…」打开——热键点一下直接把组合键按出来，还能改让位名单、
  保存目录和格式、放大镜开关、存完是否顺带复制、截图工具栏深浅配色、
  长截图的抓取间隔与每次滚动、历史截图保留几张、开机自启；点「应用」立刻生效，不用重启
- **完成**：复制到剪贴板、存成 PNG/JPG/BMP、钉到桌面。
  哪一步没成，会在工具栏下面当场说清为什么、并给一个重试的按钮，截图层不关——
  不会出现「按了没反应，图也不知道去哪了」
- **托盘常驻**：截图、历史截图、设置、打开保存目录、开机自动启动、退出

## 快捷键

| 键 | 作用 |
| --- | --- |
| `Ctrl+Alt+A` | 开始截图（QQ 在跑时让给 QQ） |
| `Enter` / `Ctrl+C` | 完成并复制到剪贴板 |
| `Ctrl+S` | 保存成文件 |
| `Ctrl+Z` | 撤销上一步标注 |
| `Delete` | 删掉选中的那条标注 |
| `C` | 放大镜里按 = 复制当前像素色号 |
| `Esc` | 先取消选中 → 再收起工具 → 才退出截图 / 关闭钉图 |
| 钉图上 `Ctrl+C` / `Ctrl+S` | 复制 / 保存钉图 |

配置在 `%APPDATA%\LikeShot\config.json`（热键、让位进程名单、保存目录和格式都能改）。

## 构建

需要 .NET 8 SDK（仅 Windows）。

```powershell
cd src\LikeShot
dotnet build
dotnet run
```

出发布包：`pack.cmd 0.1.6` 一条命令出上面那三个文件，全落在 `dist\`。
没装 Inno Setup 时两个 zip 照样出，只是跳过安装器
（装一次：`winget install JRSoftware.InnoSetup`）。安装脚本是 `installer\LikeShot.iss`，
UTF-8 带 BOM，去掉 BOM 向导里的中文会变乱码。

## 自检

没有键盘也能验证核心链路，跑完自己退出，产物在 `%TEMP%\likeshot-selftest\`：

```powershell
.\LikeShot.exe --selftest              选区 → 六种标注 → 选中已画内容（点选、拖走、拖角放大、
                                       字号滑条、Delete）→ 导出 → 撤销，外加拼接场景、退化选区、
                                       导出失败回执、历史入库、再标注、钉图窗标注与点选归属
.\LikeShot.exe --selftest --longtest   自动滚一张长页面拼出长图，再拿页面自己的可滚总高核对不重不漏
.\LikeShot.exe --selftest --historytest 历史库：入库、淘汰、收藏不参与、搜索、删除、重开还认得出
.\LikeShot.exe --selftest --historypage 把历史页灌上假数据开出来，卡片数、筛选、搜索、
                                       复制/钉图/收藏/批删/再标注、空态逐条对账
.\LikeShot.exe --selftest --hotkeytest 验热键的让位和接管
.\LikeShot.exe --selftest --settingstest 验设置窗口改完热键不用重启就生效
.\LikeShot.exe --selftest --statusshot 把「失败回执」和「成功回执」真的挂到截图层上渲染成图
.\LikeShot.exe --selftest --selectshot 把「选中框」真的画到截图层上渲染成图，看手柄大小和虚实
.\LikeShot.exe --selftest --settingsui --historyui --seedhistory --shot
                                       开设置窗自己抓一张版式图再收摊
.\LikeShot.exe --snap 路径.png          只抓一张当前屏幕，不弹界面
```

自检的历史库写在 `%TEMP%\likeshot-selftest\`，不动你真实的历史目录。

## 已知边界

- 多屏和混合 DPI 没实跑过，只在单屏 3200×2000 @175% 上验证。
- 长截图遇到一次滚超过一屏的情况，中间会缺一段（宁可缺也不重复贴）。
- 让位给 QQ 之后要连按两次才改判「它不接这个键」：第一下照旧让位并等它 0.9 秒看它弹不弹窗，
  第二下判定完会当场把这一张补给你，所以那一下会比平时慢半秒。之后就是即刻的。
- 历史截图的原图存在 `%APPDATA%\LikeShot\history\`，默认留最近 200 张（设置里可改 50/200/500），
  收藏的那几张不参与自动清理。这个目录里的图就是你的截图本身，清空之后找不回来。
- OCR（屏幕识图）、屏幕翻译、录屏没做，工具栏里也没有这几个按钮。

## 许可

MIT。工具栏图标基于 [Lucide](https://lucide.dev)（ISC 许可）绘制。

# LikeShot

[![下载最新版本](https://img.shields.io/github/v/release/doubaobaodou/LikeShot)](https://github.com/doubaobaodou/LikeShot/releases/latest)
![构建](https://github.com/doubaobaodou/LikeShot/actions/workflows/build.yml/badge.svg)
![平台](https://img.shields.io/badge/Windows-10%2F11-blue)
[![许可](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Windows 截图工具，界面和操作和 QQ 的截图工具一样。

**热键和 QQ 不冲突**：用的也是 `Ctrl+Alt+A`，但 QQ 一开就自动把这个键让回给 QQ——
QQ 开着按出来的是 QQ 的截图，QQ 没开按出来的就是这个。QQ 登录时也不会再弹「热键被占用」。

## 下载

去 [Releases](https://github.com/doubaobaodou/LikeShot/releases/latest) 拿
`...-selfcontained.zip`，解压双击 `LikeShot.exe` 就能跑，不用装任何东西；
已经装了 .NET 8 桌面运行时的可以拿 153 KB 的 `...-portable.zip`。
没有代码签名，首次运行 SmartScreen 会拦一下，点「仍要运行」。

## 功能

- **截图**：拖拽选区，自动吸附窗口边缘，放大镜显示尺寸和像素颜色
- **标注**：矩形、椭圆、箭头、画笔、马赛克（像素格 / 模糊两档）、文字、序号笔，
  16 色色板 + 三档线宽
- **长截图**：纵向 / 横向滚动拼接，可以自动滚动也可以自己滚，滚到底自动收尾
- **钉图**：把截图钉在桌面最上层，滚轮缩放、拖边角改大小，还能继续标注
- **自由截图**：选区状态下右键拖多边形，导出带透明背景
- **完成**：复制到剪贴板、存成 PNG/JPG/BMP、钉到桌面
- **托盘常驻**：截图、打开保存目录、开机自动启动、退出

## 快捷键

| 键 | 作用 |
| --- | --- |
| `Ctrl+Alt+A` | 开始截图（QQ 在跑时让给 QQ） |
| `Enter` | 完成并复制到剪贴板 |
| `Ctrl+S` | 保存成文件 |
| `Ctrl+Z` | 撤销上一步标注 |
| `C` | 放大镜里按 = 复制当前像素色号 |
| `Esc` | 退出截图 / 关闭钉图 |
| 钉图上 `Ctrl+C` / `Ctrl+S` | 复制 / 保存钉图 |

配置在 `%APPDATA%\LikeShot\config.json`（热键、让位进程名单、保存目录和格式都能改）。

## 构建

需要 .NET 8 SDK（仅 Windows）。

```powershell
cd src\LikeShot
dotnet build
dotnet run
```

## 自检

没有键盘也能验证核心链路，跑完自己退出，产物在 `%TEMP%\likeshot-selftest\`：

```powershell
.\LikeShot.exe --selftest              选区 → 六种标注 → 导出 → 撤销，外加拼接场景
.\LikeShot.exe --selftest --longtest   自动滚一张长页面拼出长图
.\LikeShot.exe --selftest --hotkeytest 验热键的让位和接管
.\LikeShot.exe --snap 路径.png          只抓一张当前屏幕，不弹界面
```

## 已知边界

- 多屏和混合 DPI 没实跑过，只在单屏 3200×2000 @175% 上验证。
- 长截图遇到一次滚超过一屏的情况，中间会缺一段（宁可缺也不重复贴）。
- OCR（屏幕识图）、屏幕翻译、录屏没做，工具栏里也没有这几个按钮。

## 许可

MIT

# Duo Click

**双键一按，框选即复制。**

一个轻量的 Windows 托盘截屏工具。按住鼠标左右键并拖动，松开两个键，截图直接进入剪贴板。随后按 **Ctrl + V**，即可粘贴到支持图片的聊天窗口、文档或图片编辑器。

## 使用

1. 启动 `DuoClick.exe`，程序驻留在系统托盘。
2. 在截图起点按住鼠标左右键（先按哪一个都可以）。
3. 保持按住并拖动，框选需要的区域。
4. 两个键都松开后，截图自动复制到剪贴板。
5. 在目标应用按 **Ctrl + V** 粘贴。

按 **Esc** 取消本次截图。右键托盘图标可暂停截屏或退出。重复启动不会产生多个实例。不自动保存图片、不联网、不上传截图，也不默认设置开机启动。

## 构建

适用于 Windows 10 / 11，需安装 .NET Framework 4.8。无需 NuGet 包或额外 .NET SDK。

在 PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

版本号保存在根目录的 `VERSION` 文件中，格式为不带前缀的 `MAJOR.MINOR.PATCH`，例如 `0.1.0`。本地构建默认使用该版本，也可临时指定版本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test -Version 0.1.0
```

构建会在 `dist/` 中生成 `DuoClick.exe`、版本元数据、测试程序（使用 `-Test` 时）以及 `DuoClick-<版本>-Windows.zip` 和对应的 `.sha256` 校验文件。ZIP 包含程序、配置文件和本 README。发布时推送形如 `v0.1.0` 的版本标签，GitHub Actions 会构建并发布 ZIP 到 GitHub Releases；用户可从该版本的 Releases 页面下载。

### 发布新版本

先把 `VERSION` 改为新版本号并推送提交，然后为该提交创建同号标签。例如发布 `0.1.1`：

```powershell
Set-Content -NoNewline -Path .\VERSION -Value '0.1.1'
git add VERSION
git commit -m "Release v0.1.1"
git push
git tag v0.1.1
git push origin v0.1.1
```

用户可在 [GitHub Releases](https://github.com/DylanZhangzzz/DuoClick/releases/latest) 下载最新 ZIP。

## 交互说明

- 两个键同时处于按下状态时开始截图；第二个键按下的位置是框选起点。系统逐个发送鼠标事件，因此不要求两次按下发生在同一毫秒。
- 松开其中一个键时仍在框选，直到两个键都松开。过小的选区直接取消，保留原剪贴板内容。
- 触发后异步截取桌面，再显示选区遮罩，避免截入遮罩；动态画面可能与按键瞬间存在短暂时间差。
- 普通单击仍会传递给前台应用。因此组合键的第一次按下也可能使前台应用获得焦点或开始选择；建议在合适的空白位置开始操作。
- 支持虚拟桌面坐标与多显示器；混合缩放、远程桌面、全屏游戏等场景仍需在实际设备上验证。
- Windows 安全桌面、受保护的视频画面及禁止截屏的窗口无法保证截取。目标应用必须支持粘贴图片。

## 项目结构

- `src/`：WinForms 托盘、鼠标手势和截图逻辑。
- `tests/`：鼠标手势状态转换回归测试。
- `build.ps1`：本地编译和测试入口。
- `app.manifest`：DPI 与普通用户权限配置。

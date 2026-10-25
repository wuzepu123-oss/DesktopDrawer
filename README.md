# Desktop Drawer

Desktop Drawer 是一个使用 C# 和 WPF 编写的 Windows 桌面侧边抽屉。它停靠在屏幕左侧，可集中放置常用程序、快捷方式、文件和文件夹，并提供磨砂背景、自动收起、图标排列和开机启动等功能。

## 主要功能

- 鼠标触碰屏幕左边缘时滑出，离开后自动收起
- 拖入程序、快捷方式、文件和文件夹，只保存引用，不移动原文件
- 图标自由排列、框选、Ctrl 多选和整组拖动
- 调整图标大小、横向/纵向间距和抽屉列数
- 显示或隐藏 Windows 桌面图标
- 可选开机自动启动
- 本地保存布局和外观设置，不依赖网络服务

## 环境要求

- Windows 10 或 Windows 11
- .NET Framework 4.x 和 WPF 组件
- 系统自带的 C# 编译器 `csc.exe`

## 构建并运行

双击 `build.bat`，脚本会调用 `build.ps1` 编译 `src/Drawer.cs`，在项目根目录生成 `Drawer.exe` 并立即启动。

也可以在 PowerShell 中运行：

```powershell
.\build.ps1
```

完整操作方法见 [使用教程.txt](使用教程.txt)。

## 项目结构

```text
DesktopDrawer/
├─ src/
│  └─ Drawer.cs
├─ docs/
│  └─ README.md
├─ build.bat
├─ build.ps1
├─ README.md
└─ 使用教程.txt
```

`Drawer.exe` 和 `drawer-error.txt` 是本地构建或运行时产生的文件，不包含在仓库中。



# 文档

- 项目概览和构建方法：仓库根目录的 `README.md`
- 完整中文操作步骤：仓库根目录的 `使用教程.txt`
- 程序源码：`src/Drawer.cs`

程序会将布局和外观设置保存在当前用户的 `%LOCALAPPDATA%\DrawerStage2` 目录。开启“开机自动启动”时，程序会写入当前用户的 Windows `Run` 注册表项，不需要管理员权限。


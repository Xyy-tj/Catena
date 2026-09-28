# 安装与依赖分发

默认交付物为 Windows x64 离线安装包 `artifacts/installer/Catena-0.6.2-win-x64-Setup.exe`，由 Inno Setup 6.7.3 编译。当前约 43.6 MB，不包含调试符号。应用和 .NET 运行时随包安装。

## 用户流程

1. 运行 Catena 安装包，允许 Windows 管理员授权。
2. 按向导完成安装。缺少 Everything 时由随包的官方安装程序静默安装，启用索引服务；已有 Everything 时保留其安装和配置。
3. 打开 Catena 并查找文件。应用自动发现默认 Everything 或 1.5a 实例；客户端未运行时后台启动已安装程序。首次建库期间可继续浏览目录。
4. 如需 AI，在设置中填写模型服务地址、模型 ID 和密钥。

安装包无需联网，组件固定为 Everything 1.4.1.1032 x64 和 ES 1.1.0.38。构建脚本按 SHA256 校验下载内容；安装阶段与便携版组件安装按钮也校验 Everything 安装程序。日常查询直接使用 Everything IPC；高级设置可填写自定义实例名，ES 仅保留作诊断和基准对照。

便携版无需预先安装 .NET。没有 Everything 时可点击设置里的「安装搜索组件」，只需响应系统权限提示。取消权限请求会显示取消状态，目录浏览和目录递归搜索仍可使用。组件安装开始后不会因关闭设置窗口而强制终止安装进程。

## 复用、升级与卸载

安装器检查 HKLM 的 32 位和 64 位 Everything 安装记录及常见安装目录。应用运行时还检查当前用户记录、默认 IPC 窗口和 1.5a 实例。已运行的默认实例优先复用，避免重复索引；快速输入或取消搜索不会反复启动客户端。

Catena 使用稳定 AppId 安装到 Program Files 下。升级保留 `%LOCALAPPDATA%/Catena` 的工作区和模型设置。卸载只删除 Catena 安装的文件与快捷方式，保留用户数据，也不停止或卸载共享 Everything。若不再需要 Everything，可从 Windows「已安装的应用」单独卸载。

## 构建

```powershell
./scripts/dev.ps1 restore
./scripts/package.ps1 -Version 0.6.2
# 自定义编译器位置
./scripts/package.ps1 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

`setup-everything.ps1` 只下载和复制发行依赖，不在开发电脑安装 Everything 服务。`package.ps1` 先发布自包含程序，再编译安装包。二进制依赖不进入 Git，许可原文进入 Git 并随应用分发。

## 验证记录与待验收项

2026-09-28 的 0.6.0 回归共 75 项全部通过，包含真实 Everything、工作区递归范围、并发查询、窗格缩放、主题、PDF/PPTX 分页、Word 原生预览和 Windows 菜单动词检查。原生 EXE 在独立数据目录正常启动并退出，输出 `CATENA_SMOKE_READY panes=2 persistence=True`。

同一份 1,000,006 条合成 EFU 清单（另有 Everything 自动补充的父目录）分别执行 30 次热查询。直接 IPC 的 P50/P95 为 11.78/16.74 ms，旧 ES 路径为 501.14/637.44 ms。统计包含结果读取和解析，不包含界面绘制或 AI 网络请求，也不代表任意真实磁盘索引。

固定便携目录为 `artifacts/win-x64`，总大小 140,885,354 字节，旧版为 219,315,170 字节。发布目录移除约 105 MB 的调试符号，同时新增 Windows PDF API 引用和背景资源。安装包为 43,648,920 字节。

当前尚未在干净 Windows 虚拟机中执行完整的提权安装、服务创建、升级和卸载，也未做安装包代码签名。这些项目须在正式发布前完成。建议分别验证无 Everything、已有 1.4、已有 1.5、取消 UAC、普通用户日常运行和重复安装六种场景。

## 官方依据

- [Everything 安装选项](https://www.voidtools.com/support/everything/command_line_options/)提供静默安装和后台启动参数。
- [Everything 服务说明](https://www.voidtools.com/en-us/support/everything/everything_service/)说明普通用户通过服务使用 NTFS 索引。
- [安装目录发现](https://www.voidtools.com/forum/viewtopic.php?t=15447)说明 InstallLocation 注册表值和 IPC 窗口查询。
- [Everything 许可](https://www.voidtools.com/License.txt)原文随包分发。
- [Inno Setup](https://jrsoftware.org/isinfo.php)用于生成安装器；常用安装页面的中文文案由本项目维护，其余诊断继承编译器默认文案。

2026-09-29 的 0.6.1 回归为 80 项通过、1 项跳过。包含真实 PowerPoint/Word 预览处理程序加载与容器缩放、预览回退及翻页、OneDrive 在线文档保护，以及目录树开启后的缩放列表对齐。Everything 外部索引集成测试本轮未启用。
最终 EXE 在独立数据目录启动并正常退出，返回码 0，输出 CATENA_SMOKE_READY panes=2 persistence=True。

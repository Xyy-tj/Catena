# Everything ES

使用 voidtools 官方 ES 1.1.0.38 x64，通过 IPC 查询用户正在运行的 Everything。启动 ES 的进程不会扫描磁盘，索引由 Everything 管理。

运行 `scripts/setup-everything.ps1` 下载客户端。脚本固定版本并校验压缩包 SHA256，二进制不进入 Git。构建时自动复制到应用的 `tools/everything`，许可证随包复制。

- 来源 https://www.voidtools.com/ES-1.1.0.38.x64.zip
- 压缩包 SHA256 `5E0C70CBF4F694080C34AA7C6C745E606C16FE76A4B5423B93EBF9DC34274C99`
- 源码 https://github.com/voidtools/ES
- 使用未修改的官方二进制，许可见 LICENSE.txt。

离线安装包另包含官方稳定版 Everything 1.4.1.1032 x64 安装程序。首次安装 Catena 时自动补齐该组件；已有 Everything 时直接复用。Everything 保留独立的 Windows 卸载入口，Catena 卸载时不删除共享组件。

- [官方安装程序](https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe)
- [官方 SHA256 清单](https://www.voidtools.com/Everything-1.4.1.1032.sha256)
- SHA256 `C42EFAD041D4C0BB4D4AC97AE7CBE89F153EC1FE078772392E749C7F5D5282D3`
- [官方许可](https://www.voidtools.com/License.txt)原文保存在 Everything-License.txt，包含 Everything 与 PCRE 声明。

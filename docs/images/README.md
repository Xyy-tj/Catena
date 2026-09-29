# README 界面图片

这些 PNG 使用 Catena 当前 Avalonia 界面和 Headless 渲染生成，包含真实控件、文件类型图标和主题。目录、文件元数据、预览文本与 AI 对话均为人工准备的演示数据，不包含用户个人文件，也未请求外部模型。

| 文件 | 内容 |
| :--- | :--- |
| `workspace-light.png` | 浅色四窗格与文本预览 |
| `workspace-dark.png` | 相同工作区的深色主题 |
| `search.png` | 文件搜索结果 |
| `ai-chat.png` | 目录组织建议的示例对话 |

截图用于说明布局和交互，不代表文件检索耗时或模型回答质量。无窗口标题栏和系统合成背景，毛玻璃效果可能与实际 Windows 桌面不同。中文、英文 README 共享这些图片；当前应用界面以中文为主。

在 Windows 上完成依赖还原后，运行 `./scripts/screenshots/generate.ps1` 可以重新生成截图。脚本借用现有 Headless 渲染环境，临时加入演示入口，结束后移除入口；不读取个人工作区或文件。

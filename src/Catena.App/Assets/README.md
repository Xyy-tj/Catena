# Catena 图标

`catena-source.png` 由内置 imagegen 生成，参考用户提供的原型左上角蓝色猫头。`catena-64.png` 用于标题栏，`catena.ico` 包含 16、24、32、48、64、128、256 px，用于窗口、EXE 与安装包。缩放保留透明通道，界面不加载大尺寸源图。

工具为内置 imagegen，没有使用 API CLI。生成提示词如下。

```text
Use case: logo-brand. Asset type: production Windows desktop application icon for Catena, a lightweight four-pane file explorer. Create one centered compact blue cat-head logo, inspired by the small blue cat mark at the top left of the reference screenshot. The reference screenshot is visual style guidance only; do NOT reproduce its interface. A friendly professional flat geometric cat head with two short pointed ears, a slightly rounded square face, two small light-blue square eyes and a tiny subtle nose. Restrained Windows Fluent feel, navy-to-medium blue palette, very simple large shapes recognizable at 16 and 24 px. Straight-on symmetrical composition, occupy 88 percent of the square canvas. Genuinely transparent background, no text, no wordmark, no border, no drop shadow, no mockup, no surrounding tile, no fine hair or whisker lines. Output a single high quality square PNG icon.
```

## 蓝雾背景

`blue-mist.png` 由内置 imagegen 生成并复制到本目录，原始输出保留。应用仅在启用该皮肤时解码一次，关闭皮肤时释放；不加载图标大尺寸源图。生成提示词如下。

```text
Create one wide 16:9 desktop application background image, 1536x864 if supported. Theme: quiet blue mist for a professional Windows file manager called Catena. Soft pale icy blue atmosphere, abstract translucent flowing sheets of frosted glass and a very faint distant mountain silhouette near the bottom right, spacious near-white center, slightly deeper muted blue around edges. Refined restrained editorial quality, smooth gradients, low contrast, no grain or busy detail, no objects, no characters, no text, no logo, no UI, no frames. This will sit behind white file browser panels, so keep calm and highly legible. Output a single background artwork.
```

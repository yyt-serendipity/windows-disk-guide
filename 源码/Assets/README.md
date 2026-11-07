# 应用图标

- `app-icon.png`：内置 imagegen 生成的透明背景原图（1254 × 1254），用于窗口和界面标识。
- `app-icon.ico`：由原图保留透明通道缩放、封装而成，包含 16、20、24、32、40、48、64、128、256 像素帧，用于 Windows 可执行文件图标。
- 图片嵌入程序，用户无需额外复制资源文件。构建不访问图片生成服务，不需要 API 密钥。

生成方式：内置 image_gen 工具，`transparent_background=true`，不是 CLI/API 回退模式。

最终生成提示词：

```text
Use case: logo-brand. Asset type: Windows desktop application icon for a friendly Chinese disk cleanup utility, 磁盘清理助手. Create ONE polished original icon: a bold rounded cobalt-blue disk drive with a simple white front slot and a single crisp cyan cleaning sparkle integrated at the upper right. Compact almost frontal view with subtle dimensional depth, clean broad surfaces and strong silhouette. Blue palette matching #2864CC and #19395F with white and a small cyan accent. No text, letters, numbers, watermark, extra objects or mockup. Square canvas; icon centered and fills about 86% of the canvas; fully transparent outside the icon, no background tile or cast shadow outside the silhouette. Must be easily recognizable at 32 pixels: large simple forms, minimal internal detail, crisp edges, professional Windows app identity. Output a finished raster icon, not a presentation sheet.
```

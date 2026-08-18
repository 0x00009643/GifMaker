# GifMaker

一个基于 WPF 的 Windows 视频转 GIF 工具，支持帧级精确预览、裁剪与导出。

## 功能

- 视频元数据探测（ffprobe 两阶段：快速元数据 + 完整帧扫描，实时进度，可取消）
- 帧级精确预览与步进（关键帧感知，逐帧跳转）
- 时间轴区间选取 + 区间循环播放
- 画面裁剪（自由拖拽 / 比例锁定）
- 预览缩放：滚轮缩放（以鼠标为中心，最高 8x）、双击重置、滚动条平移
- GIF 导出：FPS / 颜色数 / 抖动 / 循环选项，实时进度，可取消
- 导出结果预览与打开输出目录
- 单实例运行，配置持久化到应用目录 `settings.json`

## 依赖

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/)（构建）
- **FFmpeg**（运行时必需，自动检测，优先级如下）：
  1. 设置中选择的路径（工具栏"设置 ffmpeg…"）
  2. 应用目录（`ffmpeg\bin\`、`bin\`、应用根目录）
  3. PATH 环境变量
  4. 常见安装目录（`C:\ffmpeg\bin` 等）

  未找到时可点击"自动下载 ffmpeg…"（约 100MB，来自 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 的 essentials 构建，解压到应用目录 `ffmpeg\bin\`），或手动选择 ffmpeg.exe（需同目录存在 ffprobe.exe）。

## 构建与运行

```bash
dotnet build GifMaker.slnx
dotnet run --project src/GifMaker
```

## 使用说明

1. 打开视频文件，等待帧扫描完成（大文件可取消后仅用预览）
2. 通过时间轴或起止帧输入框选取范围（帧号为 1-based）
3. 可选：拖拽裁剪框调整画面，滚轮缩放查看细节
4. 设置 FPS / 颜色数 / 抖动 / 循环，点击"导出 GIF"
5. 导出完成后可预览结果或打开输出文件夹

## 技术要点

- 帧提取使用 `ffmpeg -ss <keyframe-pts> -i input -vf select='eq(n,<rel-index>)'`，在关键帧处快速定位后再精确到目标帧，避免大文件整段解码
- 探测阶段通过 ffprobe CSV 流式输出逐行解析，实现进度百分比与取消
- 预览缩放基于视口坐标系的滚动语义，支持滚动条平移与鼠标锚点缩放

## 许可证

未指定（如需要可自行添加 MIT 等许可）。
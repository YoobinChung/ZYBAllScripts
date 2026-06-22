# OptimizeCheckTool

## 工具说明

`OptimizeCheckTool` 是 Unity Editor 内的优化检测工具，用于检查工程资源、当前/构建场景以及 Play 模式运行时渲染数据中的常见性能问题。

打开入口：

`Tools -> 优化检测工具`

工具分为两个页面：

- `工程检查`：检查 URP、材质、模型、场景 Renderer/Camera、场景统计等静态问题。
- `运行时对比`：在 Play 模式下采样 Draw Calls、Batches、SetPass、Triangles，并对比优化前后数据。

## 工程检查内容

### URP Asset

检查当前渲染管线是否为 URP。

检查 URP Asset 的 MSAA 是否高于建议值。

检查 URP Asset 的 Render Scale 是否高于建议值。

检查 URP Asset 的 Soft Shadows 是否开启。

### SRP Batcher

根据当前场景情况判断 SRP Batcher 是否值得开启。

检查材质 Shader 是否疑似不适配 URP/SRP Batcher。

### Dynamic Batching

检查 URP Asset 中 `supportsDynamicBatching` 是否开启。

### GPU Instancing

检查工程材质是否开启 `Enable GPU Instancing`。

检查时会跳过包资源、模型内嵌材质、字体材质和明显不适用的 Shader。

### Depth Write

检查非透明材质的 `_ZWrite` 是否开启。

### Receive Shadows

检查场景物体引用材质上的 Receive Shadows 是否关闭。

不检查 Renderer 的 `receiveShadows`。

### Cast Shadow

检查场景内 MeshRenderer 的 Cast Shadows 是否关闭。

### Occlusion Culling

检查场景 Camera 是否开启 Occlusion Culling。

### 模型与场景统计

检查模型是否使用多个材质。

检查 Game 视图 Statistics 中的三角面数。

检查 Game 视图 Statistics 中的 Batches。

检查场景预估 Draw Call。

检查场景唯一纹理数量。

检查场景中多边形异常偏高的网格。

## 运行时对比

运行时对比页用于 Play 模式下采样实际渲染数据。

使用方式：

1. 进入 Play 模式。
2. 打开 `运行时对比` 页。
3. 点击采样基线。
4. 应用优化或切换设置。
5. 点击采样优化后。
6. 对比 Draw Calls、Batches、SetPass、Triangles 的变化。

说明：

- 运行时数据来自 Unity ProfilerRecorder。
- 若采样不到数据，请确认场景正在 Play 模式运行。
- 合批结果仍建议结合 Profiler 和 Frame Debugger 判断。

## 自动修复说明

部分检查项支持自动修复：

- URP MSAA、Render Scale、Soft Shadows。
- SRP Batcher 按建议单独开启或关闭。
- Dynamic Batching。
- GPU Instancing。
- Depth Write。
- 材质 Receive Shadows。
- MeshRenderer Cast Shadows。
- Camera Occlusion Culling。

注意：

- 涉及场景写入或材质修改的修复需要退出 Play 模式后执行。
- 底部“一键全部”只会执行适合批量处理的修复项。
- SRP Batcher、Cast Shadows、Receive Shadows 等需要确认处理方式的项目不会被静默强制处理。
- 工具提供“撤销上一次自动修复”，但涉及场景/资源保存后的回退仍建议配合版本管理确认。

## 扫描范围

场景扫描顺序：

1. 当前活动场景。
2. Build Settings 中启用的场景。
3. 如果前两者为空，则扫描 `Assets` 下所有 `.unity` 场景。

资源扫描默认跳过：

- `Packages`
- `_Packages`
- `PackageCache`
- `JMO Assets`
- 模型内嵌材质
- 字体资源派生材质，如 `.ttf`、`.otf`、`.ttc`

## 安装方法

1. 将 `OptimizeCheckTool.cs` 放入 Unity 工程的 `Assets` 目录下。
2. 推荐路径：

   `Assets/_Res/ZYB/Scripts/OptimizeCheckTool.cs`

3. 等待 Unity 自动编译。
4. 编译完成后打开：

   `Tools -> 优化检测工具`

如果菜单没有出现，请先确认 Unity Console 中没有脚本编译错误。

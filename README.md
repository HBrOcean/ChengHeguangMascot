# ChengHeguangMascot

一只住在桌面上的 **程和光**（《重返未来：1999》）Q 版桌面宠物。透明无边框、可置顶、随意拖动，单击会播动作，支持多套皮肤切换和全局快捷键。

| | |
|---|---|
| **技术栈** | .NET 10 · Avalonia 11.3 · spine-csharp 4.2 · SkiaSharp · SharpHook |
| **平台** | Windows（完整功能）· macOS / Linux 可运行，窗口特效与开机自启仅 Windows 生效 |
| **模型** | UTTU Archive · chibi Spine 4.2（313701 / 313702 / 313703） |

## 下载

到 [Releases](https://github.com/HBrOcean/ChengHeguangMascot/releases) 下载 `ChengHeguangMascot-win-x64.zip`，解压后双击 `ChengHeguangMascot.exe` 即可。

自包含单文件，**不需要另外安装 .NET 运行时**。

## 特性

- 30fps 实时渲染 Spine 动画，透明背景，不占任务栏
- 贴图统一按 RGBA8888 + Premul 解码，透明区域不会出现色块或黑边
- 启动时先播一遍入场动画（born），之后自然转入待机
- 拖动移动、位置记忆；**允许拖到屏幕外**，不吸附、不回弹
- 单击播放动作，播完自动回待机
- **定时随机动作**：默认每 20 秒动一次，间隔 5~180 秒可调、可关闭；任何一次手动动作都会重新计时
- **窗口随动作幅度自适应**：动作大时窗口向四周扩张（立即生效），动作结束后约 1 秒才开始回缩
- 右键菜单：设置 / 播放动作 / 重置位置 / 隐藏 / 退出
- 皮肤自动扫描 + 卡片网格预览，托盘与设置面板双入口切换
- 全局快捷键播放指定动作，可视化编辑，支持一键恢复默认
- 单实例保护：重复启动会**唤醒已在运行的实例**（弹出设置面板），不会开出第二只

## 默认快捷键

全部为 `Ctrl + 键`，可在「设置 → 按键」里改成任意组合（如 `Ctrl+Shift+A`）。

| 快捷键 | 动作 | 快捷键 | 动作 |
|---|---|---|---|
| `Ctrl + Y` | 打开设置面板 | `Ctrl + H` | hit |
| `Ctrl + N` | 关闭设置面板 | `Ctrl + I` | idle |
| `Ctrl + 1` | skill1 | `Ctrl + P` | posture |
| `Ctrl + 2` | skill2 | `Ctrl + S` | sleep |
| `Ctrl + 3` | skill3 | `Ctrl + B` | born |
| `Ctrl + D` | die | `Ctrl + G` | giddy |
| `Ctrl + U` | unique | | |

> 全局钩子只监听、不拦截：在编辑器里按 `Ctrl+S` / `Ctrl+B` 会同时触发编辑器自身的功能，介意的话改成 `Ctrl+Shift+字母`。

## 皮肤

启动时扫描 `Assets/characters/` 下的**每一个子目录**：目录里凑齐了同名的 `.atlas` 和 `.skel`，就算一套可用皮肤。代码里没有任何硬编码的皮肤白名单，多一套目录就多一张卡片。

随包附带三套：

| ID | 名称 | 说明 |
|---|---|---|
| 313701 | 程和光 · 默认 | 默认皮肤 |
| 313702 | 檐外鹭 | 洞悉 II |
| 313703 | 第三秒 | 时装 |

显示名按三级兜底（前一级为空才往下退）：

1. `config.json` 里的 `SkinNames`（`{"313701": "我的名字"}`）
2. 皮肤目录里的 `display.txt`（UTF-8，取第一行非空内容）
3. 目录名本身

资源不齐（缺 `.atlas` 或 `.skel`）的目录仍会出现在列表里，但会**置灰、标注「资源缺失」且无法选中**，方便你看出来少了什么。

### 添加自己的皮肤

1. 在 `Assets/characters/` 下新建子目录，目录名就是皮肤 ID（例如 `my_pet`）
2. 放入同名文件：`my_pet.atlas`、`my_pet.skel`，以及 atlas 里声明的图集
3. 想自定义显示名就放一个 `display.txt`
4. 打开设置 → **皮肤** → 点「刷新列表」

> atlas 里的图集贴图请**保持原样**，不要重新打包：spine 4.x 的 mesh UV 是烘焙进 `.skel` 的整页归一化坐标，移动图集区域会让整只角色碎成拼图。
>
> 本项目的 spine-csharp 运行时已额外支持 atlas 中 `rotate: 180` / `rotate: 270` 的区域（UTTU 图集大量使用），官方运行时只处理 90。

设置面板里的预览图是运行时用同一套渲染器离屏渲染的（图集是散件拼图，不能直接当缩略图），首帧后缓存到配置目录；换过图集对不上时点「重建预览图」。

## 配置

配置文件：`%AppData%\ChengHeguangMascot\config.json`（macOS / Linux 为 `~/.config/ChengHeguangMascot/`）

所有选项都能在设置面板里改，改完即时生效。日志在同目录的 `log.txt`（里面有包围盒、锚点、视框、按键命中的完整记录），排查问题先看它。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
# Windows
build.bat

# macOS / Linux
chmod +x build.sh && ./build.sh

# 或者直接
dotnet run
```

打包成自包含单文件 exe：

```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```

推送到 GitHub 后，Actions 会在 `windows-latest` 上构建；打 `v*` 标签会自动发 Release：

```bash
git tag v0.1.0
git push origin v0.1.0
```

`tools/push.bat` 可以在 Windows 上一键初始化并推送（`push.bat "提交信息"`，不带参数时用带日期的默认信息）。`tools/push-termux.sh` 是手机侧的推送辅助脚本 —— Termux 只能推送，不能构建（Avalonia 要桌面图形栈、SkiaSharp 没有 android-arm64 RID、SharpHook 依赖 libuiohook）。

## 常见问题

**桌宠不见了 / 点到屏幕外了**
按 `Ctrl + Y` 打开设置，或点托盘图标；也可以右键菜单或托盘里的「重置位置」把它拉回主屏右下角。位置极端异常（换过显示器、拔掉副屏）时，启动会自动救援到主屏右下角。

**快捷键没反应**
打开 `log.txt` 看有没有 `[Key] 匹配 ...` 记录。没有记录说明按键被系统或其他软件拦截；有记录但不触发，检查绑定格式是否为 `修饰键 + Vc键名`（如 `Ctrl+Vc1`），设置面板里格式非法会标红。

**动作播到一半就回待机**
关掉「动作锁定」以外的干扰项：随机池排除了 idle / channel_idle / born / sleep / die / posture / freeze，以及任何时长 ≤ 0.05 秒的动画。如果你自己加了新皮肤、动画名不在排除表里，它就会被抽到。

**窗口会突然变大**
这是「窗口随动作自适应」：大动作（die 整只左躺、skill1 挥剑）需要更大的画布才不会裁到角色。窗口背景是透明的，所以你看不到边框，只看到动作完整播完。不想要这种效果可以在设置里关掉。

**macOS / Linux 上鼠标穿透、置底、开机自启无效**
这些依赖 Win32 API，非 Windows 平台会记录日志后忽略，不影响其它功能。

## 素材与致谢

- 角色模型：**UTTU Archive** 的 `chibi` Spine 资源（`313701` / `313702` / `313703`），版权归《重返未来：1999》/ 深蓝互动所有，仅供个人非商业使用
- Spine 运行时：[esotericsoftware/spine-runtimes](https://github.com/EsotericSoftware/spine-runtimes)（4.2，C# 版源码内置于 `Spine/`）
- 角色锚点、逐帧包围盒采样、动态视框等几何方案见 `Rendering/SkeletonModel.cs` 与 `Rendering/PetRenderControl.cs` 的注释

本项目代码以 MIT 许可发布，**模型与美术素材不在 MIT 许可范围内**，详见 LICENSE。

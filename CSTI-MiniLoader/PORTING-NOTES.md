# CSTI-MiniLoader → MelonLoader 0.6.5 / .NET 8 移植说明（task-2）

原始工程：`D:\RiderProjects\CSTI-ModLoader\CSTI-MiniLoader`（net472 + Unhollower，只读参考）
移植产物：`D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06`

## 1. 构建

```
cd D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06
dotnet build -c Release -p:ML06=D:\RiderProjects\ml-installer-06
```

产物：`bin\Release\CSTI-MiniLoader.dll`（net8.0，`AppendTargetFrameworkToOutputPath=false`）
同时输出依赖：`LitJSON.dll` / `LZ4.dll` / `LZ4pn.dll` / `NAudio.dll` / `System.Resources.Extensions.dll`
（已复制到 `userlibs\`，部署说明见 `userlibs\README.txt`）

csproj 要点：`net8.0`、`AllowUnsafeBlocks`、`AppendTargetFrameworkToOutputPath=false`、
`EnableDynamicLoading`、`CopyLocalLockFileAssemblies`、`DefineConstants=TRACE;MELON_LOADER`、
`Nullable=annotations`（保留 `?` 注解但不产生可空告警）；
引用 `$(ML06)\mldata\MelonLoader\net8\{MelonLoader,0Harmony,Il2CppInterop.Runtime}.dll`
与 `$(ML06)\interop_out\*.dll`；`tools\**` 被 `Compile Remove` 排除。

## 2. 机械转换清单

| 转换 | 位置 |
| --- | --- |
| `using UnhollowerBaseLib;` → `Il2CppInterop.Runtime.InteropTypes[.Arrays]` | LoadResources / LoadPatchMain / MainGen / MainGenTools / WarpFunc |
| `using UnhollowerRuntimeLib;` → `using Il2CppInterop.Runtime;` | LoadArchMod / LoadResources / LoadPatchMain / MainGenTools |
| `MelonUtils.GetApplicationPath()` → `MelonEnvironment.GameRootDirectory` + `using MelonLoader.Utils` | LoadArchMod.cs |
| `MelonHandler.ModsDirectory` → `MelonEnvironment.ModsDirectory`（并去掉多余的 Path.Combine） | LoadArchMod.cs |
| `Object.FindObjectsOfTypeAll(...)` → `Resources.FindObjectsOfTypeAll(...)` | LoadPatchMain.cs ×3（0.6 的 `UnityEngine.Object` 没有该方法，只有 `Resources` 有） |
| `.get_Item(i)` → `[i]`；`.set_Item(i,v)` → `[i] = v` | LoadPatchMain.cs ×2（`List<string>`）、WarpFunc.cs ×2（`Il2CppSystem.Collections.IList`） |
| `Il2CppReferenceArray<T>(0)` 写法 | 源码中不存在该写法（只有 `(IntPtr)` 重载），无需改；保留任务规则备查 |

**目标框架无差异项（已逐项核对，未改）**：`Il2CppType.Of<T>()` / `Il2CppType.From(Type)` 在 Il2CppInterop
里返回 `Il2CppSystem.Type`，与 `FindObjectsOfType(Il2CppSystem.Type)`、`ScriptableObject.CreateInstance(Il2CppSystem.Type)`
签名直接匹配；`Il2CppSystem.Array.Resize<T>(ref Il2CppArrayBase<T>, int)`、`IL2CPP.*`、`Il2CppObjectBase.Cast<T>()`
均在；`NativeFieldInfoPtr_*` 仍是 `static readonly IntPtr`（MainGen 的反射取偏移逻辑成立）；
`MelonInfoAttribute(Type,string,string,string,string=默认)` 允许 4 参写法；
`MelonLogger.Msg(object)` / `Error(object)` 允许传 `StringBuilder` / `Exception`；`HarmonyDontPatchAll` 仍在。
没有 BepInEx 分支，故未使用 `#if !MELON_LOADER`。

## 3. 图片链路：已接入真实原生通道（不再是 stub）

托管 `Texture2D` 会触发 Mono 缺陷并 SIGABRT，因此移植版**完全不再引用该类型**——
`CSTI-MiniLoader.dll` 的 IL 里 `Texture2D` 的 TypeRef 计数为 **0**（仅剩日志字符串）。

- `RawTexture.cs`：task-1（raw-interop）交付的固定 API，**逐字拷贝**自
  `mods-06\RawTextureTest\RawTexture.cs`（同步方法见 `RawTexture.SOURCE.txt`）。
  提供 `CreateTexture2D(int,int)` / `CreateTexture2D(int,int,int,bool)` / `LoadImage` /
  `CreateSprite` / `Wrap` + `Log` / `LastError` / `Describe()` 等诊断。
- `RawTextureRaw.cs`：本工程补充的 **raw 上传 + Apply**（本地文件，待 raw-interop 归并）：
  `LoadRawTextureData(IntPtr, byte[])`（原生 `Texture2D.LoadRawTextureData(IntPtr,int)`）与
  `Apply(IntPtr, bool, bool)`（原生 `Texture2D.Apply(bool,bool)`）。
- 调用点（`LoadUtil/LoadArchMod.cs`）：
  - `ProbeTextureApis()`：`RawTexture.Describe()` + `CreateTexture2D(4,4,RGBA32,false)` +
    `RawTextureRaw.LoadRawTextureData` + `Apply` + `CreateSprite` + `Wrap`，结果写入游戏根目录 `csti_probe.log`
  - `LoadImgBLK_V2()` 的 `itemFlg == 1`（单图）
  - `LoadImgBLK_V2()` 的 `itemFlg == 2`（图集）
- 每个调用点都有“返回空即跳过该资源 + Warning（含 `LastError`）”的防御，不会崩。

### 仍未闭环的点

| # | 位置 | 说明 |
| --- | --- | --- |
| 1 | `RawTextureRaw.cs` | `LoadRawTextureData` / `Apply` 目前在本工程本地实现；建议并回 `RawTexture.cs`（并回后删掉本文件、把 4 处 `RawTextureRaw.X` 改回 `RawTexture.X`） |
| 2 | 单图路径 | 原版单图 `Sprite.Create(..., pivot=Vector2.zero)`，而 `CreateSprite` 固定用 pivot(0.5,0.5)；图集原版是 pivot(0.5,0.5)/ppu100/FullRect，与现实现一致。单图 pivot 差异待真机观察是否需要扩展 API |
| 3 | 图集纹理名 | 原版会设置 `Texture2D.name`；现在只设置 `Sprite.name`（精灵名字典才是关键），纹理名丢失不影响功能 |
| 4 | mip 链 | 实测图集 payload = `w*h*4/3`（DXT5 带 mip 链），而纹理按 `mipChain=false` 创建、原样上传整段字节。若真机报数据长度错误，改为只上传 mip0（`w*h` 字节）或按 `mipChain=true` 建纹理 |

## 4. 依赖（详见 userlibs/README.txt）

| 依赖 | 版本 | 状态 |
| --- | --- | --- |
| LitJSON | 0.19.0（net8.0 资产） | 有；需放 `UserLibs` |
| LZ4 (lz4net) | 1.0.15.93（该包只有 netstandard1.0） | 有；`LZ4.dll` + `LZ4pn.dll` 都要放 `UserLibs` |
| NAudio | 1.10.0（netstandard2.0） | 有；WAV 可用，MP3 在 Android 上大概率不可用（走 Windows ACM/DMO） |
| System.Resources.Extensions | 4.7.0 | 传递依赖，可选放入 `UserLibs` |

**没有缺失的依赖**；不需要 `MelonLoader/Dependencies`，0.6.5 的 `UserLibs` 目录是正解
（启动日志会打印 `Loading UserLibs from '<...>/UserLibs'`，且 UserLibs 里的 DLL 不会被当成 mod）。

## 5. 离线验证证据（无 adb，真机由 lead 负责）

`tools\DependencySmoke`（独立 net8 控制台工程，链接移植工程里纯托管的
`KVProvider.cs / StringMapper.cs / MapperObject.cs`）：

```
cd tools\DependencySmoke
dotnet run -c Release -- "D:\RiderProjects\csti\BepInEx\plugins\普通的头像包.modArch_V3"
```

结果（全部 PASS）：
1. LZ4：真实 7 参重载 `LZ4Codec.Decode(src,0,len,dst,0,dstLen,true)` 往返 262144 字节逐字节一致；
2. LitJSON：`JsonData` 解析/索引/类型判定正常；
3. NAudio：`WaveFileReader` + `ReadNextSampleFrame()` 读出 800 采样；
4. 真实 42.9MB 包：容器分帧 + ImgBLK/JsonsBLK/LocalBLK/LuaBLK 全部子块 LZ4 解码长度与声明一致；
   ImgBLK 解析后缓冲被**完整消费**（解析布局与打包器一致）；JsonsBLK 17 个对象用**移植工程自己的
   MapperObject/StringMapper** 解析成功，`ToJson()` 产物 100% 能被 `System.Text.Json` 判定为合法 JSON；
   图集实测 `8192x8192 fmt=12(DXT5) len=89478512(=w*h*4/3)`，首 8 字节 `FF FF 49 92 24 49 92 24`
   是合法 DXT5 alpha 块 → **原始 GPU 字节，不是 PNG/JPG**。

另：`CSTI-MiniLoader.dll` 的 IL 层核对结果——
`Texture2D` TypeRef = 0、`Unhollower*` TypeRef = 0、AssemblyRef 只有
`MelonLoader / 0Harmony / Il2CppInterop.Runtime / Assembly-CSharp / Il2Cppmscorlib / UnityEngine.{CoreModule,AudioModule,JSONSerializeModule} / LitJSON / LZ4 / NAudio` + net8 框架程序集；
`MelonInfo` 5 参、基类 `MelonLoader.MelonMod`、`TargetFramework=.NETCoreApp,Version=v8.0`。

## 6. 已知风险 / 原始代码疑点（**未改**，供 lead 决策）

1. **`Traverse...Field("ExplicitPageContent")` 恒为 null**（`LoadPatchMain.cs` 内 `WaiterForContentDisplayer`）：
   0.5.7 与 0.6 的代理类里这两个成员都是**属性**（实测两版都只有 `NativeFieldInfoPtr_*` 字段），
   所以 `Traverse.Create(displayer).Field<List<ContentPage>>("ExplicitPageContent").Value` 拿不到东西，
   `pages.Clear()` 会 NRE，被外层 try/catch 吞掉 → “Default ContentPage”那条链路实际不生效。
   **原版就有的缺陷，不是移植回归**；一行可修（`displayer.ExplicitPageContent` / `displayer.DefaultPage = modPage`）。
2. **`MainGenTools.SetLiByWarpper<T>` 用 `MainGen.GetOrGen(typeof(T))` 而不是 `baseObj.GetType()`**（原版如此），
   字段查表大概率落空 → List 型字段的 warp 变成静默 no-op。同为原版缺陷。
3. Android 上 NAudio 的 MP3 解码大概率抛异常：移植版把**每条音频**的解码包进 try/catch
   （读取已完成、流位置安全），失败只跳过该条并打 Warning，不会像原版那样中断整个包的加载。
4. `OnInitializeMelon` 里就调 `LoadAndInit()`，此时 `GameLoad.Instance` 可能还是 null；
   `LoadResources.LoadGameResource()` 自带 try/catch，不会中断后续包加载。
5. 若 ML 运行时侧 Il2CppInterop 版本不匹配（启动日志里
   `Il2CppInteropFixes/DotnetAssemblyLoadContextFix` 报 `RegisterTypeOptions.set_LogSuccess` MissingMethod），
   Harmony 对 il2cpp 方法的 patch 可能不生效，本 mod 的 3 个 HarmonyPatch 也就不会触发——属运行时侧，本工程无法验证。

## 7. 真机日志关键字

`[STEP] 0..9`（LoadAndInit 各阶段）、`[ARCH] 扫描目录` / `加载模组`、`[IMG]` / `[IMG2]`、
`RawTexture.Describe()` 段与 `RawTexture` / `RawTextureRaw` 的 `LastError`、
游戏根目录下的 `csti_probe.log`（探针逐行落盘，崩溃前最后一行不丢）。

# metadump —— interop 程序集元数据读取器（离线根因定位工具）

## 为什么需要它

2026-10-03 的 `prop=null` ×95,180 卡点，此前的两次判断都是错的：

1. "制品与源码不一致" ✗（其实是我读错了日志文件）；
2. "interop 没给这些结构字段生成属性" ✗（其实是**生成了 public 字段、根本没生成属性**）。

第 2 条只有**直接读设备上真正加载的那份 interop 程序集**才能证实 —— 这个工具就是干这个的：
只读 `System.Reflection.Metadata`（PE/CLI 元数据），**不加载程序集、不执行代码**，因此可以在桌面上
对 `MelonLoader/Il2CppAssemblies/*.dll` 直接取证。

## 用法

```powershell
# 类型过滤：打印匹配类型的是否值类型 / 基类 / 全部字段 / 全部属性
dotnet run -c Release -- <assembly.dll> DurabilitiesConditions

# 成员过滤（@ 开头）：列出"哪个类型拥有该类型/名字的字段或属性"
dotnet run -c Release -- <assembly.dll> @DurabilitiesConditions
dotnet run -c Release -- <assembly.dll> CardData @CardName

# 设备侧取证（先拉回来）
adb pull /sdcard/MelonLoader/<包名>/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
dotnet run -c Release -- Assembly-CSharp.dll @EnemySkillModifier
```

## 本轮结论（可复现）

| il2cpp 值类型 | 形态 | 成员 |
|---|---|---|
| `LocalizedString` / `DurabilityConditions` / `CardInteractionTrigger`（含引用） | `Il2CppSystem.ValueType` 派生的**类** | **属性** |
| `DurabilitiesConditions` / `DurabilityWeightValue` / `EncounterVariable` / `EnemySkillModifier` / `EnemyWoundModifier` / `LightSourceSettings` / `SimpleHitProbabilityModifier`（blittable） | `System.ValueType`（`ExplicitLayout`）的 **C# struct** | **public 字段，无属性** |

设备侧 `UnityEngine.CoreModule.dll`：`Vector2` = `public float x/y`；`Vector2Int` = `public int m_X/m_Y` + 属性 `x/y`。
结论与修复见 `TASK4-FINDINGS.md §24`。

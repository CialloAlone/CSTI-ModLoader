using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// 加载结果自检：不依赖玩家进游戏，直接打印
    ///   · MiniLoader 各类型字典里到底注册了多少对象（mod 内容是否真的进来）
    ///   · 游戏自己的 UniqueIDScriptable.AllUniqueObjects 在 mod 加载前后有没有增长
    ///   · 抽样几个键名，便于确认确实是 Windy 的内容
    /// </summary>
    public static class SelfCheck
    {
        private static int _registryBefore = -1;

        public static void CaptureBefore()
        {
            try { _registryBefore = UniqueIDScriptable.AllUniqueObjects?.Count ?? -1; }
            catch { _registryBefore = -1; }
        }

        public static void Dump()
        {
            try
            {
                MelonLogger.Msg("===== [自检] mod 加载结果 =====");

                var keys = MiniLoader.AllItemDictionary.Keys.ToList();
                MelonLogger.Msg("[自检] MiniLoader 字典类型数 = " + keys.Count);
                foreach (var k in keys)
                {
                    var d = MiniLoader.AllItemDictionary[k];
                    MelonLogger.Msg("[自检]   " + k.Name + " : " + (d?.Count ?? 0) + " 项");
                }

                MelonLogger.Msg("[自检] AllGUIDDict = " + MiniLoader.AllGUIDDict.Count
                                + " 项；AllScriptableObjectDict = " + MiniLoader.AllScriptableObjectDict.Count
                                + " 项；CustomGameObjectListDict = " + MiniLoader.CustomGameObjectListDict.Count
                                + " 项；CustomContentDisplayerDict = " + MiniLoader.CustomContentDisplayerDict.Count + " 项");

                // 抽样：看名字像不像 mod 内容
                foreach (var k in keys)
                {
                    var d = MiniLoader.AllItemDictionary[k];
                    if (d == null || d.Count == 0) continue;
                    if (MiniLoader.DiagFull)
                    {
                        var sample = d.Keys.Take(6).ToList();
                        MelonLogger.Msg("[自检]   " + k.Name + " 抽样: " + string.Join(", ", sample));
                        if (k.Name == "Sprite" && d.Count > 0 && d.Count <= 200)
                            MelonLogger.Msg("[自检]   Sprite 全量键(" + d.Count + "): " + string.Join(", ", d.Keys));
                    }

                    // 顺带确认：这些对象是否真的进了游戏的注册表
                    int inGame = 0;
                    try
                    {
                        var reg = UniqueIDScriptable.AllUniqueObjects;
                        if (reg != null) foreach (var id in d.Keys) { if (id != null && reg.ContainsKey(id)) inGame++; }
                    }
                    catch { }
                    MelonLogger.Msg("[自检]   " + k.Name + " 已进入游戏注册表 " + inGame + " / " + d.Count);
                }

                int after = -1;
                try { after = UniqueIDScriptable.AllUniqueObjects?.Count ?? -1; } catch { }
                MelonLogger.Msg("[自检] 游戏注册表: mod 加载前 " + _registryBefore + " → 现在 " + after
                                + "（增量 " + ((_registryBefore >= 0 && after >= 0) ? (after - _registryBefore).ToString() : "?") + "）");
                MelonLogger.Msg("===== [自检] 结束 =====");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[自检] 失败: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}

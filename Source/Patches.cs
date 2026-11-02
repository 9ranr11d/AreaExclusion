using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AreaExclusion
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            new Harmony("9ranr11d.AreaExclusion").PatchAll();
        }
    }

    // 모든 허가 구역 판정(ForbidUtility·길찾기·지역 겹침)이 이 게터를 거치므로 여기서 합성 구역으로 바꿔치기한다
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap), MethodType.Getter)]
    public static class Patch_EffectiveArea
    {
        public static void Postfix(Pawn_PlayerSettings __instance, ref Area __result)
        {
            // null 은 "제한 없음" 이거나 "구역을 안 따름" — 후자면 그대로 둔다
            if (__result == null && !__instance.RespectsAllowedArea)
            {
                return;
            }
            __result = Exclusions.Effective(__instance, __result);
        }
    }

    // 바닐라 세터는 "새 구역 == 실효 구역" 일 때만 작업을 끊는데, 제외가 있으면 실효 구역이 합성 구역이라 비교가 늘 어긋난다
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
    public static class Patch_SetArea
    {
        public static void Postfix(Pawn_PlayerSettings __instance, Area value)
        {
            Pawn pawn = Exclusions.PawnOf(__instance);
            if (pawn == null || Exclusions.For(pawn) == null)
            {
                return;
            }
            // 허가 구역으로 고른 구역이 제외 목록에 있으면 뺀다 (SetExcluded 가 작업 중단도 처리)
            if (value != null && Exclusions.IsExcluded(pawn, value))
            {
                Exclusions.SetExcluded(pawn, value, false);
            }
            else
            {
                Exclusions.InterruptIfOutside(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.ExposeData))]
    public static class Patch_Settings_ExposeData
    {
        public static void Postfix(Pawn_PlayerSettings __instance) => Exclusions.ExposeData(__instance);
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Notify_AreaRemoved))]
    public static class Patch_Settings_AreaRemoved
    {
        public static void Postfix(Pawn_PlayerSettings __instance, Area area) => Exclusions.Notify_AreaRemoved(__instance, area);
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Notify_MapRemoved))]
    public static class Patch_Settings_MapRemoved
    {
        public static void Postfix(Pawn_PlayerSettings __instance, Map map) => Exclusions.Notify_MapRemoved(__instance, map);
    }

    // 원본 구역의 칸이 바뀌면 그 구역에 기대는 합성 구역의 같은 칸을 다시 계산한다
    [HarmonyPatch(typeof(Area), "Set")]
    public static class Patch_Area_Set
    {
        public static void Postfix(Area __instance, IntVec3 c)
        {
            if (!(__instance is Area_Excluded))
            {
                CompositeAreas.Notify_CellChanged(__instance, c);
            }
        }
    }

    [HarmonyPatch(typeof(Area), nameof(Area.Invert))]
    public static class Patch_Area_Invert
    {
        public static void Postfix(Area __instance) => CompositeAreas.Notify_AllChanged(__instance);
    }

    [HarmonyPatch(typeof(Area), nameof(Area.Clear))]
    public static class Patch_Area_Clear
    {
        public static void Postfix(Area __instance) => CompositeAreas.Notify_AllChanged(__instance);
    }

    [HarmonyPatch(typeof(AreaManager), "Remove")]
    public static class Patch_AreaManager_Remove
    {
        public static void Postfix(Area area) => CompositeAreas.Notify_AreaRemoved(area);
    }

    // 합성 구역은 저장하지 않는다 — 불러온 뒤 필요할 때 다시 만든다
    [HarmonyPatch(typeof(AreaManager), nameof(AreaManager.ExposeData))]
    public static class Patch_AreaManager_ExposeData
    {
        public static void Prefix(AreaManager __instance, out List<Area> __state)
        {
            __state = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                __state = __instance.AllAreas.FindAll(a => a is Area_Excluded);
                __instance.AllAreas.RemoveAll(a => a is Area_Excluded);
            }
        }

        // 저장 중 예외가 나도 되돌려야 정착민 캐시가 가리키는 합성 구역이 길찾기에서 빠지지 않는다
        public static void Finalizer(AreaManager __instance, List<Area> __state)
        {
            if (__state != null)
            {
                __instance.AllAreas.AddRange(__state);
            }
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
    public static class Patch_Game_RemoveMap
    {
        public static void Postfix(Map map) => CompositeAreas.Notify_MapRemoved(map);
    }

    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    public static class Patch_Game_LoadGame
    {
        public static void Prefix()
        {
            Exclusions.Reset();
            CompositeAreas.Reset();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class Patch_Game_InitNewGame
    {
        public static void Prefix()
        {
            Exclusions.Reset();
            CompositeAreas.Reset();
        }
    }

    // 정보창의 허가 구역 막대 라벨에 제외 구역을 붙인다
    [HarmonyPatch(typeof(AreaUtility), nameof(AreaUtility.AreaAllowedLabel))]
    public static class Patch_AreaAllowedLabel
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            List<Area> excluded = Exclusions.For(pawn);
            if (excluded != null)
            {
                __result += " − " + string.Join(", ", excluded.ConvertAll(a => a.Label));
            }
        }
    }
}

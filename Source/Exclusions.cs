using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AreaExclusion
{
    // 허가 구역에서 제외 구역들을 뺀 합성 구역. 바닐라 길찾기(AreaSource)는 AreaManager 에 등록된 구역만
    // 비트맵을 만들어 두므로 이 구역도 AllAreas 에 넣는다. 저장할 때는 빼서 세이브에 흔적을 남기지 않는다.
    public class Area_Excluded : Area
    {
        public readonly Area allowed; // null = 제한 없음
        public readonly List<Area> excluded;
        public readonly string key;

        public Area_Excluded(AreaManager manager, Area allowed, List<Area> excluded, string key) : base(manager)
        {
            this.allowed = allowed;
            this.excluded = excluded;
            this.key = key;
            // 새 구역은 아직 어떤 캐시(지역 겹침·길찾기)에도 없으니 격자에 직접 쓴다
            BoolGrid grid = InnerGrid;
            int count = manager.map.cellIndices.NumGridCells;
            for (int i = 0; i < count; i++)
            {
                if (Compute(i))
                {
                    grid[i] = true;
                }
            }
        }

        public override string Label =>
            AreaUtility.AreaAllowedLabel_Area(allowed) + " − " + string.Join(", ", excluded.Select(a => a.Label));

        public override Color Color => allowed?.Color ?? Color.gray;

        public override int ListPriority => 0;

        public override string GetUniqueLoadID() => "AreaExcluded_" + ID;

        public IEnumerable<Area> Sources => allowed == null ? excluded : excluded.Prepend(allowed);

        private bool Compute(int index)
        {
            if (allowed != null && !allowed[index])
            {
                return false;
            }
            for (int i = 0; i < excluded.Count; i++)
            {
                if (excluded[i][index])
                {
                    return false;
                }
            }
            return true;
        }

        // 인덱서 쓰기는 Set 을 거치므로 바뀐 칸만 지역·길찾기 캐시에 통지된다
        public void Recompute(IntVec3 c)
        {
            int index = Map.cellIndices.CellToIndex(c);
            this[index] = Compute(index);
        }

        public void RecomputeAll()
        {
            int count = Map.cellIndices.NumGridCells;
            for (int i = 0; i < count; i++)
            {
                this[i] = Compute(i);
            }
        }
    }

    // 맵별 합성 구역 캐시. (허가 구역, 제외 구역 집합) 조합 하나당 하나만 만든다.
    public static class CompositeAreas
    {
        private static readonly Dictionary<Map, Dictionary<string, Area_Excluded>> byMap =
            new Dictionary<Map, Dictionary<string, Area_Excluded>>();

        private static readonly Dictionary<Area, List<Area_Excluded>> dependents =
            new Dictionary<Area, List<Area_Excluded>>();

        public static Area_Excluded Get(Map map, Area allowed, List<Area> excluded)
        {
            string key = (allowed?.ID ?? -1) + "|" + string.Join(",", excluded.Select(a => a.ID).OrderBy(id => id));
            if (!byMap.TryGetValue(map, out var dict))
            {
                dict = new Dictionary<string, Area_Excluded>();
                byMap[map] = dict;
            }
            if (!dict.TryGetValue(key, out var comp))
            {
                comp = new Area_Excluded(map.areaManager, allowed, new List<Area>(excluded), key);
                map.areaManager.AllAreas.Add(comp);
                dict[key] = comp;
                foreach (Area source in comp.Sources)
                {
                    if (!dependents.TryGetValue(source, out var list))
                    {
                        list = new List<Area_Excluded>();
                        dependents[source] = list;
                    }
                    list.Add(comp);
                }
            }
            return comp;
        }

        public static void Notify_CellChanged(Area area, IntVec3 c)
        {
            if (dependents.Count == 0 || !dependents.TryGetValue(area, out var list))
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Recompute(c);
            }
        }

        public static void Notify_AllChanged(Area area)
        {
            if (dependents.TryGetValue(area, out var list))
            {
                foreach (Area_Excluded comp in list)
                {
                    comp.RecomputeAll();
                }
            }
        }

        public static void Notify_AreaRemoved(Area area)
        {
            if (dependents.TryGetValue(area, out var list))
            {
                foreach (Area_Excluded comp in list.ToList())
                {
                    Remove(comp);
                }
            }
        }

        public static void Notify_MapRemoved(Map map)
        {
            if (byMap.TryGetValue(map, out var dict))
            {
                foreach (Area_Excluded comp in dict.Values.ToList())
                {
                    Remove(comp);
                }
                byMap.Remove(map);
            }
        }

        public static void Reset()
        {
            byMap.Clear();
            dependents.Clear();
        }

        private static void Remove(Area_Excluded comp)
        {
            comp.Map.areaManager.AllAreas.Remove(comp);
            if (byMap.TryGetValue(comp.Map, out var dict))
            {
                dict.Remove(comp.key);
            }
            foreach (Area source in comp.Sources)
            {
                if (dependents.TryGetValue(source, out var list))
                {
                    list.Remove(comp);
                    if (list.Count == 0)
                    {
                        dependents.Remove(source);
                    }
                }
            }
            Exclusions.version++;
        }
    }

    public class ExclusionData
    {
        public readonly Dictionary<Map, List<Area>> byMap = new Dictionary<Map, List<Area>>();

        public int cacheVersion = -1;
        public Map cacheMap;
        public Area cacheAllowed;
        public Area_Excluded cacheComposite;
    }

    // 정착민별·맵별 제외 구역. Pawn_PlayerSettings 에 필드를 못 붙이니 옆 사전에 둔다.
    // 빈 목록은 남기지 않는다 — 사전에 있으면 제외 구역이 하나 이상 있다는 뜻.
    public static class Exclusions
    {
        // 제외 목록이나 합성 구역이 바뀔 때마다 올려서 정착민별 캐시를 무효화한다
        public static int version;

        private static readonly Dictionary<Pawn_PlayerSettings, ExclusionData> table =
            new Dictionary<Pawn_PlayerSettings, ExclusionData>();

        private static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Pawn> pawnRef =
            AccessTools.FieldRefAccess<Pawn_PlayerSettings, Pawn>("pawn");

        public static Pawn PawnOf(Pawn_PlayerSettings settings) => pawnRef(settings);

        public static List<Area> For(Pawn pawn)
        {
            Map map = pawn.MapHeld;
            if (pawn.playerSettings == null || map == null || !table.TryGetValue(pawn.playerSettings, out var data))
            {
                return null;
            }
            return data.byMap.TryGetValue(map, out var list) ? list : null;
        }

        public static bool IsExcluded(Pawn pawn, Area area) => For(pawn)?.Contains(area) ?? false;

        public static void SetExcluded(Pawn pawn, Area area, bool excluded)
        {
            if (excluded == IsExcluded(pawn, area))
            {
                return;
            }
            if (excluded)
            {
                if (!table.TryGetValue(pawn.playerSettings, out var data))
                {
                    data = new ExclusionData();
                    table[pawn.playerSettings] = data;
                }
                if (!data.byMap.TryGetValue(area.Map, out var list))
                {
                    list = new List<Area>();
                    data.byMap[area.Map] = list;
                }
                list.Add(area);
            }
            else
            {
                RemoveWhere(pawn.playerSettings, (map, a) => a == area);
            }
            version++;
            InterruptIfOutside(pawn);
        }

        public static bool ClearAll(Pawn pawn)
        {
            Map map = pawn.MapHeld;
            if (map == null || For(pawn) == null)
            {
                return false;
            }
            RemoveWhere(pawn.playerSettings, (m, a) => m == map);
            version++;
            return true;
        }

        public static void Notify_AreaRemoved(Pawn_PlayerSettings settings, Area area)
        {
            if (RemoveWhere(settings, (map, a) => a == area))
            {
                version++;
            }
        }

        public static void Notify_MapRemoved(Pawn_PlayerSettings settings, Map map)
        {
            if (RemoveWhere(settings, (m, a) => m == map))
            {
                version++;
            }
        }

        private static bool RemoveWhere(Pawn_PlayerSettings settings, System.Func<Map, Area, bool> match)
        {
            if (!table.TryGetValue(settings, out var data))
            {
                return false;
            }
            bool removed = false;
            foreach (Map map in data.byMap.Keys.ToList())
            {
                List<Area> list = data.byMap[map];
                removed |= list.RemoveAll(a => match(map, a)) > 0;
                if (list.Count == 0)
                {
                    data.byMap.Remove(map);
                }
            }
            if (data.byMap.Count == 0)
            {
                table.Remove(settings);
            }
            return removed;
        }

        // 바닐라가 고른 허가 구역(null 포함)에 제외 구역을 적용한 결과. 게터가 매우 자주 불리므로 캐시한다.
        public static Area Effective(Pawn_PlayerSettings settings, Area vanilla)
        {
            if (table.Count == 0 || !table.TryGetValue(settings, out var data))
            {
                return vanilla;
            }
            Map map = pawnRef(settings).MapHeld;
            if (map == null || !data.byMap.TryGetValue(map, out var list))
            {
                return vanilla;
            }
            if (data.cacheVersion != version || data.cacheMap != map || data.cacheAllowed != vanilla)
            {
                // 로딩 중에는 맵·ID 관리자가 덜 준비돼 있으니 합성 구역을 만들지 않는다
                if (Current.ProgramState != ProgramState.Playing)
                {
                    return vanilla;
                }
                data.cacheComposite = CompositeAreas.Get(map, vanilla, list);
                data.cacheVersion = version;
                data.cacheMap = map;
                data.cacheAllowed = vanilla;
            }
            // 허가 구역이 전부 제외되면 바닐라는 빈 구역을 "제한 없음" 으로 취급하므로, 그때는 제외를 무시한다
            return data.cacheComposite.TrueCount > 0 ? data.cacheComposite : vanilla;
        }

        public static void InterruptIfOutside(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Drafted || pawn.jobs?.curJob == null)
            {
                return;
            }
            Area area = pawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap;
            if (area != null && area.TrueCount > 0 && pawn.jobs.curJob.AnyTargetOutsideArea(area))
            {
                pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
            }
        }

        public static void ExposeData(Pawn_PlayerSettings settings)
        {
            List<Map> maps = null;
            List<Area> areas = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 제외 구역이 없으면 노드 자체를 쓰지 않는다 — 모드를 빼도 세이브가 바닐라 그대로
                if (!table.TryGetValue(settings, out var data))
                {
                    return;
                }
                maps = new List<Map>();
                areas = new List<Area>();
                foreach (var kv in data.byMap)
                {
                    foreach (Area area in kv.Value)
                    {
                        maps.Add(kv.Key);
                        areas.Add(area);
                    }
                }
            }
            Scribe_Collections.Look(ref maps, "aex_excludedMaps", LookMode.Reference);
            Scribe_Collections.Look(ref areas, "aex_excludedAreas", LookMode.Reference);
            // 참조 목록은 이 단계에서만 풀린 값이 지역 변수로 돌아온다(지역 변수는 단계마다 새로 null)
            if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs && maps != null && areas != null)
            {
                table.Remove(settings);
                var data = new ExclusionData();
                for (int i = 0; i < maps.Count && i < areas.Count; i++)
                {
                    // 사라진 맵·구역은 null 로 들어오므로 건너뛴다
                    if (maps[i] == null || areas[i] == null)
                    {
                        continue;
                    }
                    if (!data.byMap.TryGetValue(maps[i], out var list))
                    {
                        list = new List<Area>();
                        data.byMap[maps[i]] = list;
                    }
                    if (!list.Contains(areas[i]))
                    {
                        list.Add(areas[i]);
                    }
                }
                if (data.byMap.Count > 0)
                {
                    table[settings] = data;
                }
                version++;
            }
        }

        public static void Reset()
        {
            table.Clear();
            version++;
        }
    }
}

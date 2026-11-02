using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AreaExclusion
{
    // 배정 탭·정착민 정보의 구역 버튼. 바닐라 DoAreaSelector 를 그대로 옮기고 Shift+클릭 제외만 더했다.
    // 버튼 배치(DoAllowedAreaSelectors)는 바닐라 그대로라 칸 수가 늘거나 스크롤이 생기지 않는다.
    [HarmonyPatch(typeof(AreaAllowedGUI), "DoAreaSelector")]
    public static class Patch_DoAreaSelector
    {
        private enum DragMode
        {
            None,
            Allow,
            Exclude,
            Unexclude
        }

        private static DragMode drag;

        private static readonly Color ExcludedShade = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ExcludedLine = new Color(0.95f, 0.25f, 0.2f);

        public static bool Prefix(Rect rect, Pawn p, Area area)
        {
            DoAreaSelector(rect, p, area);
            return false;
        }

        private static void DoAreaSelector(Rect rect, Pawn p, Area area)
        {
            MouseoverSounds.DoRegion(rect);
            rect = rect.ContractedBy(1f);
            GUI.DrawTexture(rect, (area != null) ? area.ColorTexture : BaseContent.GreyTex);
            bool excluded = area != null && Exclusions.IsExcluded(p, area);
            if (excluded)
            {
                Widgets.DrawBoxSolid(rect, ExcludedShade);
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            string text = AreaUtility.AreaAllowedLabel_Area(area);
            Rect labelRect = rect;
            labelRect.xMin += 3f;
            labelRect.yMin += 2f;
            Widgets.Label(labelRect, text);
            if (excluded)
            {
                Widgets.DrawLine(new Vector2(rect.x, rect.yMax), new Vector2(rect.xMax, rect.y), ExcludedLine, 2f);
                GUI.color = ExcludedLine;
                Widgets.DrawBox(rect, 2);
                GUI.color = Color.white;
            }
            Area current = p.playerSettings.AreaRestrictionInPawnCurrentMap;
            if (current == area)
            {
                Widgets.DrawBox(rect, 2);
            }
            if (Event.current.rawType == EventType.MouseUp && Event.current.button == 0)
            {
                drag = DragMode.None;
            }
            if (!Input.GetMouseButton(0) && Event.current.type != EventType.MouseDown)
            {
                drag = DragMode.None;
            }
            if (Mouse.IsOver(rect))
            {
                // 현재 허가 구역에 올리면 제외가 적용된 실제 범위를 보여 준다
                if (area != null && area == current && Exclusions.For(p) != null)
                {
                    p.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap?.MarkForDraw();
                }
                else
                {
                    area?.MarkForDraw();
                }
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    drag = DragMode.Allow;
                    if (Event.current.shift)
                    {
                        drag = DragMode.None;
                        if (area == null)
                        {
                            if (Exclusions.ClearAll(p))
                            {
                                SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
                            }
                        }
                        else if (area == current)
                        {
                            SoundDefOf.ClickReject.PlayOneShotOnCamera();
                        }
                        else
                        {
                            // 처음 누른 버튼의 상태로 방향을 정하고, 끌고 지나가는 버튼에 같은 방향을 적용한다
                            drag = excluded ? DragMode.Unexclude : DragMode.Exclude;
                        }
                    }
                }
                switch (drag)
                {
                    case DragMode.Allow:
                        if (current != area)
                        {
                            p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                            SoundDefOf.Designate_DragStandard_Changed_NoCam.PlayOneShotOnCamera();
                        }
                        break;
                    case DragMode.Exclude:
                        if (area != null && area != current && !excluded)
                        {
                            Exclusions.SetExcluded(p, area, true);
                            SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera();
                        }
                        break;
                    case DragMode.Unexclude:
                        if (excluded)
                        {
                            Exclusions.SetExcluded(p, area, false);
                            SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
                        }
                        break;
                }
            }
            Text.Anchor = TextAnchor.UpperLeft;
            string tip = text;
            if (excluded)
            {
                tip += "\n" + "AreaExclusion_Excluded".Translate().Colorize(ExcludedLine);
            }
            tip += "\n\n" + (area == null ? "AreaExclusion_TipClear" : "AreaExclusion_Tip").Translate();
            TooltipHandler.TipRegion(rect, tip);
        }
    }
}

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FogOfWarAcrossLevels
{
    // Surveillance Shift (ferny.SurveillanceShift) builds a "visible right now" grid per map, but
    // only from each viewer's own level. As above, So below II (astryl.AsAboveSoBelow2) stacks
    // the levels of a map as horizontal bands of the same map, so that grid never crosses levels:
    // a colonist on a roof could not see the enemies AASB2 draws below through the open air.
    //
    // Just before Surveillance Shift turns the grid into fog (MapComponent_FogOfWar.ApplyFog),
    // this adds the cells seen across levels:
    //  - down: a visible upper cell that shows the level below (open air or wall top) reveals
    //    the cell AASB2 draws there, the same rule as ABBands.TryResolveVisibleBelow;
    //  - up: a visible cell with open air above it is an opening; through it the viewer sees the
    //    non-air cells of the upper level within `upDepth` tiles of the opening, if nothing on
    //    that level blocks the line (as in AASB2's ABShaft cross-level shots).
    // Only cells seen directly count as a source, so the two directions never feed each other.
    // Both mods are reached by name, with no compile-time reference.
    [StaticConstructorOnStartup]
    public static class LevelVision
    {
        private const string Tag = "[Fog of War Across Levels] ";

        private static FieldInfo bandCountField;
        private static FieldInfo bandHeightField;
        private static PropertyInfo slotProperty;
        private static Type bandMapType;
        private static TerrainDef openAir;
        private static TerrainDef wallTop;

        private class MapState
        {
            public bool[] source;
            public bool[] added;
            public bool fresh = true;
        }

        private static readonly ConditionalWeakTable<Map, MapState> States = new();

        static LevelVision()
        {
            try
            {
                Init();
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "disabled, setup failed: " + e);
            }
        }

        private static void Init()
        {
            var fogType = AccessTools.TypeByName("SurveillanceShift.MapComponent_FogOfWar");
            bandMapType = AccessTools.TypeByName("AsAboveSoBelow.ABBandMap");
            if (fogType == null || bandMapType == null)
            {
                Log.Warning(Tag + "disabled: Surveillance Shift or As above, So below II is not loaded.");
                return;
            }

            var applyFog = AccessTools.Method(fogType, "ApplyFog");
            var recalc = AccessTools.Method(fogType, "RecalculateVisibility");
            var visibleField = AccessTools.Field(fogType, "visibleCells");
            bandCountField = AccessTools.Field(bandMapType, "bandCount");
            bandHeightField = AccessTools.Field(bandMapType, "bandHeight");
            slotProperty = AccessTools.Property(bandMapType, "Slot");
            openAir = DefDatabase<TerrainDef>.GetNamedSilentFail("AB_OpenAir");
            wallTop = DefDatabase<TerrainDef>.GetNamedSilentFail("AB_WallTop");

            if (applyFog == null || recalc == null || visibleField?.FieldType != typeof(bool[])
                || bandCountField == null || bandHeightField == null || slotProperty == null || openAir == null)
            {
                Log.Warning(Tag + "disabled: Surveillance Shift or As above, So below II changed its internals "
                    + $"(ApplyFog={applyFog != null}, RecalculateVisibility={recalc != null}, visibleCells={visibleField != null}, "
                    + $"bandCount={bandCountField != null}, bandHeight={bandHeightField != null}, Slot={slotProperty != null}, AB_OpenAir={openAir != null}).");
                return;
            }

            ShootVision.Init();
            var harmony = new Harmony("henrique.fogofwaracrosslevels");
            harmony.Patch(recalc, prefix: new HarmonyMethod(typeof(LevelVision), nameof(Recalculate_Prefix)));
            harmony.Patch(applyFog, prefix: new HarmonyMethod(typeof(LevelVision), nameof(ApplyFog_Prefix)));
            Log.Message(Tag + "active.");
        }

        // RecalculateVisibility rebuilds the grid from scratch, so every cell in it is seen directly.
        public static void Recalculate_Prefix(MapComponent __instance)
        {
            if (States.TryGetValue(__instance.map, out var state)) state.fresh = true;
        }

        public static void ApplyFog_Prefix(MapComponent __instance, bool[] ___visibleCells)
        {
            var map = __instance.map;
            if (map == null || ___visibleCells == null) return;

            // Only right after a recalculation: a later ApplyFog (scope overlay) still has these
            // cells in the grid. They count as seen directly, so they also feed the level vision.
            if (FowalMod.Settings.seeShootable && States.GetValue(map, _ => new MapState()).fresh)
            {
                try
                {
                    var bands = TryGetBands(map, out var count, out var height, out var slot)
                        ? new ShootVision.Bands { slot = slot, height = height, count = count }
                        : default;
                    ShootVision.Reveal(map, ___visibleCells, bands);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(Tag + "failed to reveal what colonists can shoot: " + e, 0x46_4F_57_42);
                }
            }

            try
            {
                Extend(map, ___visibleCells);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "failed to extend visibility: " + e, 0x46_4F_57_41);
            }
        }

        private static void Extend(Map map, bool[] visible)
        {
            var settings = FowalMod.Settings;
            if (map == null || visible == null || (settings.seeDown is false && settings.seeUp is false)) return;

            if (TryGetBands(map, out var bandCount, out var bandHeight, out var slot) is false) return;

            var count = map.cellIndices.NumGridCells;
            if (visible.Length != count) return;

            var state = States.GetValue(map, _ => new MapState());
            if (state.source == null || state.source.Length != count)
            {
                state.source = new bool[count];
                state.added = new bool[count];
                state.fresh = true;
            }

            // Sources: what the viewers see directly. A second ApplyFog without a recalculation
            // (Surveillance Shift's scope overlay) still has our cells from the last pass in the grid.
            var source = state.source;
            var added = state.added;
            if (state.fresh)
            {
                Array.Copy(visible, source, count);
                Array.Clear(added, 0, count);
                state.fresh = false;
            }
            else
            {
                for (var i = 0; i < count; i++) source[i] = visible[i] && added[i] is false;
            }

            var ctx = new Context
            {
                map = map,
                terrain = map.terrainGrid,
                visible = visible,
                added = added,
                sizeX = map.Size.x,
                sizeZ = map.Size.z,
                slot = slot,
                bandHeight = bandHeight,
                bandCount = Math.Min(bandCount, (map.Size.z + slot - 1) / slot),
                upDepth = Math.Max(1, Math.Min(settings.upDepth, FowalSettings.MaxUpDepth)),
            };

            for (var band = 0; band < ctx.bandCount; band++)
            {
                var z0 = band * slot;
                var z1 = Math.Min(z0 + bandHeight, ctx.sizeZ);
                for (var z = z0; z < z1; z++)
                {
                    var row = z * ctx.sizeX;
                    for (var x = 0; x < ctx.sizeX; x++)
                    {
                        if (source[row + x] is false) continue;
                        if (settings.seeDown && band > 0) LookDown(ref ctx, x, z);
                        if (settings.seeUp && band < ctx.bandCount - 1) LookUp(ref ctx, x, z);
                    }
                }
            }
        }

        // The levels As above, So below II stacks on this map; slot is 0 if there is only one.
        private static bool TryGetBands(Map map, out int bandCount, out int bandHeight, out int slot)
        {
            bandCount = bandHeight = slot = 0;
            var bands = map.GetComponent(bandMapType);
            if (bands == null) return false;
            bandCount = (int)bandCountField.GetValue(bands);
            bandHeight = (int)bandHeightField.GetValue(bands);
            if (bandCount <= 1 || bandHeight <= 0) return false;
            slot = (int)slotProperty.GetValue(bands);
            return slot > 0;
        }

        private struct Context
        {
            public Map map;
            public TerrainGrid terrain;
            public bool[] visible;
            public bool[] added;
            public int sizeX;
            public int sizeZ;
            public int slot;
            public int bandHeight;
            public int bandCount;
            public int upDepth;
        }

        private static bool ShowsBelow(TerrainDef t) => t != null && (t == openAir || t == wallTop);

        private static void Mark(ref Context ctx, int index)
        {
            if (ctx.visible[index]) return;
            ctx.visible[index] = true;
            ctx.added[index] = true;
        }

        // Same walk as AASB2's ABBands.ResolveVisibleBelowUncached: go down one band at a time
        // while the cell shows the one below it; every cell passed is on screen, so reveal it.
        private static void LookDown(ref Context ctx, int x, int z)
        {
            if (ShowsBelow(ctx.terrain.TerrainAt(z * ctx.sizeX + x)) is false) return;
            var zz = z;
            while (true)
            {
                zz -= ctx.slot;
                if (zz < 0) return;
                var index = zz * ctx.sizeX + x;
                Mark(ref ctx, index);
                if (zz / ctx.slot <= 0 || ShowsBelow(ctx.terrain.TerrainAt(index)) is false) return;
            }
        }

        // Go up one band at a time while the cell above is open air (a ceiling or roof ends it).
        // Each open-air cell is an opening into that level.
        private static void LookUp(ref Context ctx, int x, int z)
        {
            var zz = z;
            while (true)
            {
                zz += ctx.slot;
                var band = zz / ctx.slot;
                if (zz >= ctx.sizeZ || band >= ctx.bandCount) return;
                var index = zz * ctx.sizeX + x;
                if (ctx.terrain.TerrainAt(index) != openAir) return;
                Mark(ref ctx, index);
                ScanAroundOpening(ref ctx, x, zz, band);
            }
        }

        private static void ScanAroundOpening(ref Context ctx, int ox, int oz, int band)
        {
            var depth = ctx.upDepth;
            var depthSq = depth * depth + depth;
            var bandMinZ = band * ctx.slot;
            var bandMaxZ = Math.Min(bandMinZ + ctx.bandHeight, ctx.sizeZ) - 1;
            var opening = new IntVec3(ox, 0, oz);
            for (var dz = -depth; dz <= depth; dz++)
            {
                var z = oz + dz;
                if (z < bandMinZ || z > bandMaxZ) continue;
                for (var dx = -depth; dx <= depth; dx++)
                {
                    if (dx * dx + dz * dz > depthSq) continue;
                    var x = ox + dx;
                    if (x < 0 || x >= ctx.sizeX) continue;
                    var index = z * ctx.sizeX + x;
                    if (ctx.visible[index] || ctx.terrain.TerrainAt(index) == openAir) continue;
                    var cell = new IntVec3(x, 0, z);
                    if (GenSight.LineOfSight(opening, cell, ctx.map, skipFirstCell: true)) Mark(ref ctx, index);
                }
            }
        }
    }
}

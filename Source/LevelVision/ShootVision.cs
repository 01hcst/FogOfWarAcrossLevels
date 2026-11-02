using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FogOfWarAcrossLevels
{
    // Surveillance Shift casts each colonist's view from the colonist's own cell only. Vanilla
    // shooting also lets the shooter lean out to a free neighbour cell (and lets the target be hit
    // where it leans out), so a colonist next to a window edge can shoot an enemy the fog still
    // hides. Rule here: if a colonist has a line of fire to a creature, the creature's cell is
    // revealed. On the same level that is vanilla's line of fire; between levels of As above,
    // So below II it is AASB2's (ABCombatV2.SolveWithLeaning): both sides lean the vanilla way and
    // the line must pass through an opening found by ABShaft.TrySolve.
    internal static class ShootVision
    {
        private const int FallbackViewDistance = 30;

        private static Func<Pawn, int> sightRadius;
        private static Func<bool> perspectiveShiftAuthentic;
        private static Func<Pawn> perspectiveShiftAvatar;
        private static Func<bool> crossLevelCombatEnabled;
        private static MethodInfo shaftSolve;
        private static readonly object[] ShaftArgs = new object[7];

        private static readonly List<(IntVec3 pos, int radius)> Viewers = new();
        private static readonly List<IntVec3> Sources = new();
        private static readonly List<IntVec3> Dests = new();
        private static readonly List<IntVec3> Lean = new();

        // AASB2's LeanCells: when the other side is straight above or below, lean every way.
        private static readonly IntVec3[] Quadrants =
        {
            new(5, 0, 5), new(5, 0, -5), new(-5, 0, -5), new(-5, 0, 5),
        };

        // The levels of the map, as AASB2's ABBandMap lays them out (slot 0: a single level).
        public struct Bands
        {
            public int slot;
            public int height;
            public int count;

            public bool Banded => slot > 0 && count > 1;
            public int BandOf(IntVec3 c) => Banded ? Math.Clamp(c.z / slot, 0, count - 1) : 0;
            public bool InGutter(IntVec3 c) => Banded && c.z % slot >= height;
            public IntVec3 Translate(IntVec3 c, int band) => Banded ? new IntVec3(c.x, c.y, band * slot + c.z % slot) : c;
        }

        // Optional hooks into Surveillance Shift: its sight radius and its Perspective Shift
        // avatar mode. Without them this falls back to a plain radius and all colonists.
        public static void Init()
        {
            sightRadius = Delegate<Func<Pawn, int>>("SurveillanceShift.State:SightRadius");
            perspectiveShiftAuthentic = Delegate<Func<bool>>("SurveillanceShift.ModCompatibility:IsPerspectiveShiftAuthentic");
            perspectiveShiftAvatar = Delegate<Func<Pawn>>("SurveillanceShift.ModCompatibility:GetPerspectiveShiftAvatar");

            // Cross-level lines of fire. Without these, only same-level lines are checked.
            crossLevelCombatEnabled = Delegate<Func<bool>>("AsAboveSoBelow.ABCombatV2:get_Enabled");
            var solution = AccessTools.TypeByName("AsAboveSoBelow.ABShotSolution");
            if (solution != null)
            {
                shaftSolve = AccessTools.Method("AsAboveSoBelow.ABShaft:TrySolve", new[]
                {
                    typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(float), typeof(float), typeof(bool), solution.MakeByRefType(),
                });
                if (shaftSolve?.ReturnType != typeof(bool)) shaftSolve = null;
            }
            if (shaftSolve == null) Log.Warning("[Fog of War Across Levels] As above, So below II changed its internals: lines of fire between levels will not reveal anything.");
        }

        private static T Delegate<T>(string name) where T : Delegate
        {
            try
            {
                return AccessTools.Method(name)?.CreateDelegate(typeof(T)) as T;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static void Reveal(Map map, bool[] visible, Bands bands)
        {
            if (visible.Length != map.cellIndices.NumGridCells) return;
            CollectViewers(map);
            if (Viewers.Count == 0) return;
            var crossLevel = bands.Banded && shaftSolve != null && crossLevelCombatEnabled?.Invoke() != false;

            foreach (var target in map.mapPawns.AllPawnsSpawned)
            {
                if (target.Faction is { IsPlayer: true }) continue;
                var cell = target.Position;
                var index = map.cellIndices.CellToIndex(cell);
                if (visible[index]) continue;
                if (bands.InGutter(cell)) continue;
                var targetBand = bands.BandOf(cell);
                foreach (var (pos, radius) in Viewers)
                {
                    if (pos == cell || bands.InGutter(pos)) continue;
                    var band = bands.BandOf(pos);
                    bool seen;
                    if (band == targetBand)
                    {
                        seen = (cell - pos).LengthHorizontalSquared <= radius * radius && CanShoot(pos, cell, map);
                    }
                    else
                    {
                        seen = crossLevel && CrossLevelDistance(bands, pos, band, cell, targetBand) <= radius
                            && CanShootAcross(pos, band, cell, targetBand, map, bands);
                    }
                    if (seen is false) continue;
                    visible[index] = true;
                    break;
                }
            }
        }

        // Same viewers as Surveillance Shift's RecalculateVisibility, minus those who cannot shoot.
        private static void CollectViewers(Map map)
        {
            Viewers.Clear();
            if (perspectiveShiftAuthentic?.Invoke() == true)
            {
                var avatar = perspectiveShiftAvatar?.Invoke();
                if (avatar is { Spawned: true, Dead: false } && avatar.Map == map) AddViewer(avatar);
                return;
            }
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead is false) AddViewer(pawn);
            }
        }

        private static void AddViewer(Pawn pawn)
        {
            if (pawn.Downed) return;
            var radius = sightRadius?.Invoke(pawn)
                ?? Mathf.RoundToInt(FallbackViewDistance * pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight));
            if (radius > 0) Viewers.Add((pawn.Position, radius));
        }

        // Vanilla Verb.TryFindShootLineFromTo for a pawn shooting a pawn, without range or weapon:
        // from the shooter's cell, then from each cell it can lean out to, to any cell the target
        // can be hit at (its own cell or where it leans out).
        private static bool CanShoot(IntVec3 root, IntVec3 target, Map map)
        {
            if (CanHitFrom(root, target, map)) return true;
            ShootLeanUtility.LeanShootingSourcesFromTo(root, target, map, Sources);
            foreach (var source in Sources)
            {
                if (CanHitFrom(source, target, map)) return true;
            }
            return false;
        }

        private static bool CanHitFrom(IntVec3 source, IntVec3 target, Map map)
        {
            ShootLeanUtility.LeanShootingSourcesFromTo(target, source, map, Dests);
            foreach (var dest in Dests)
            {
                if (GenSight.LineOfSight(source, dest, map, skipFirstCell: true)) return true;
            }
            return false;
        }

        // AASB2's ABShaft.CrossBandDistance: distance on the map plus 3 tiles per level.
        private static float CrossLevelDistance(Bands bands, IntVec3 root, int rootBand, IntVec3 target, int targetBand)
        {
            return (bands.Translate(target, rootBand) - root).LengthHorizontal + 3f * Math.Abs(targetBand - rootBand);
        }

        // AASB2's ABCombatV2.SolveWithLeaning for a pawn shooting a pawn on another level,
        // without range (checked by the caller) or weapon.
        private static bool CanShootAcross(IntVec3 root, int rootBand, IntVec3 target, int targetBand, Map map, Bands bands)
        {
            Sources.Clear();
            Sources.Add(root);
            LeanCells(root, bands.Translate(target, rootBand), map, Lean);
            foreach (var cell in Lean)
            {
                if (cell != root && OnBand(map, bands, cell, rootBand) && Sources.Contains(cell) is false) Sources.Add(cell);
            }

            foreach (var source in Sources)
            {
                LeanCells(target, bands.Translate(source, targetBand), map, Lean);
                foreach (var dest in Lean)
                {
                    if (OnBand(map, bands, dest, targetBand) && ShaftSolves(map, source, dest)) return true;
                }
            }
            return false;
        }

        private static bool ShaftSolves(Map map, IntVec3 source, IntVec3 dest)
        {
            ShaftArgs[0] = map;
            ShaftArgs[1] = source;
            ShaftArgs[2] = dest;
            ShaftArgs[3] = float.MaxValue;
            ShaftArgs[4] = 0f;
            ShaftArgs[5] = false;
            ShaftArgs[6] = null;
            return (bool)shaftSolve.Invoke(null, ShaftArgs);
        }

        private static bool OnBand(Map map, Bands bands, IntVec3 c, int band)
        {
            return c.InBounds(map) && bands.BandOf(c) == band && bands.InGutter(c) is false;
        }

        private static void LeanCells(IntVec3 at, IntVec3 toward, Map map, List<IntVec3> outCells)
        {
            if (toward != at)
            {
                ShootLeanUtility.LeanShootingSourcesFromTo(at, toward, map, outCells);
                return;
            }
            outCells.Clear();
            foreach (var quadrant in Quadrants)
            {
                ShootLeanUtility.LeanShootingSourcesFromTo(at, at + quadrant, map, Dests);
                foreach (var cell in Dests)
                {
                    if (outCells.Contains(cell) is false) outCells.Add(cell);
                }
            }
        }
    }
}

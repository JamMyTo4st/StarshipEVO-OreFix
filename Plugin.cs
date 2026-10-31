using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Mathematics;
using World.Terrain;

namespace OreFix
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> FixDigChain, CleanOnScan, RemoveAirMarkers, SnapMarkers, Verbose;

        private void Awake()
        {
            Log = Logger;
            FixDigChain = Config.Bind("Fixes", "FixMinedOreMarkerRemoval", true,
                "Fix vanilla bug: when the first ore of a terrain node is mined, the node's ore-marker list head is updated on a struct COPY and lost, leaving a dangling/recycled id (ghost markers, markers that never go away).");
            CleanOnScan = Config.Bind("Fixes", "CleanOrphanMarkersOnScan", true,
                "When you press the scan key, release every ore-scanner entry not owned by any live terrain node (ghost markers).");
            RemoveAirMarkers = Config.Bind("Fixes", "RemoveMarkersInAir", true,
                "On scan, remove markers whose voxel (via the game's own voxel density data) is now air (density >= 0), i.e. floating markers over dug/changed terrain.");
            SnapMarkers = Config.Bind("Fixes", "SnapMarkersToVoxel", true,
                "On scan, remove the vanilla random +/-1.2 m jitter so each marker sits at the centre of its ore voxel (inside the terrain) instead of possibly hovering above the surface.");
            Verbose = Config.Bind("Debug", "VerboseLogging", true, "Log every fix to BepInEx/LogOutput.log.");
            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"OreFix {MyPluginInfo.PLUGIN_VERSION} loaded. FixDigChain={FixDigChain.Value} CleanOnScan={CleanOnScan.Value} RemoveAir={RemoveAirMarkers.Value} Snap={SnapMarkers.Value}");
        }

        internal static void V(string s) { if (Verbose.Value) Log.LogInfo(s); }

        /// Remove every entry with coordinate from a node's chain. Returns removed count.
        internal static int RemoveFromChain(ref int rootId, Vector3i coord)
        {
            var datas = OreScannerResources.datas;
            int removed = 0, prev = 0, cur = rootId, guard = 0;
            while (cur != 0 && guard++ < 100000)
            {
                OreScannerData d = datas[cur];
                int next = d.nextElementId;
                if (d.coordinate == coord)
                {
                    if (prev == 0) rootId = next;
                    else { var p = datas[prev]; p.nextElementId = next; datas.SetAt(prev, p); }
                    datas.ReleaseId(cur);
                    removed++;
                }
                else prev = cur;
                cur = next;
            }
            return removed;
        }
    }

    // ---------- Fix 1: CheckIfDropRock passes OctreeNodeData by value ----------
    [HarmonyPatch(typeof(OctreeNodeResources), "CheckIfDropRock")]
    internal static class Patch_CheckIfDropRock
    {
        [ThreadStatic] internal static int CurrentNodeId;

        static void Prefix(int nodeId) => CurrentNodeId = nodeId;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var target = AccessTools.Method(typeof(OreScannerResources), nameof(OreScannerResources.UpdateData));
            var repl = AccessTools.Method(typeof(Patch_CheckIfDropRock), nameof(UpdateDataFixed));
            int n = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(target)) { ci.operand = repl; n++; }
                yield return ci;
            }
            Plugin.Log.LogInfo($"[OreFix] CheckIfDropRock transpiler replaced {n} UpdateData call(s) (expected 1).");
        }

        internal static readonly Dictionary<int, int> PendingRoot = new Dictionary<int, int>();

        public static void UpdateDataFixed(ref int copyRoot, Vector3i coord)
        {
            if (!Plugin.FixDigChain.Value) { OreScannerResources.UpdateData(ref copyRoot, coord); return; }
            int nodeId = CurrentNodeId;
            var nodes = OctreeNodeResources.OctreeNodeDatas;
            OctreeNodeData nd = nodes[nodeId];
            int oldRoot = nd.oreScannerDataId;
            int root = oldRoot;
            int removed = Plugin.RemoveFromChain(ref root, coord);
            nd.oreScannerDataId = root;
            nodes[nodeId] = nd;
            copyRoot = root;
            PendingRoot[nodeId] = root;
            if (removed > 0)
                Plugin.V($"[OreFix] Mined ore voxel {coord.x},{coord.y},{coord.z} on node {nodeId}: removed {removed} marker(s); chain head {oldRoot} -> {root}{(oldRoot != root ? " (vanilla would have lost this head change)" : "")}");
        }
    }

    // UpdateDensitiesOnNode writes back its stale copy of the node; re-apply the corrected head.
    [HarmonyPatch(typeof(OctreeNodeResources), "UpdateDensitiesOnNode")]
    internal static class Patch_UpdateDensitiesOnNode
    {
        static void Postfix(int nodeId)
        {
            if (Patch_CheckIfDropRock.PendingRoot.Count == 0) return;
            if (Patch_CheckIfDropRock.PendingRoot.TryGetValue(nodeId, out int root))
            {
                var nodes = OctreeNodeResources.OctreeNodeDatas;
                var nd = nodes[nodeId];
                nd.oreScannerDataId = root;
                nodes[nodeId] = nd;
            }
            Patch_CheckIfDropRock.PendingRoot.Clear();
        }
    }

    // ---------- Fix 2/3/4: validate scanner data when scanning ----------
    [HarmonyPatch(typeof(OreScannerResources), nameof(OreScannerResources.OnScannerKeyPressed))]
    internal static class Patch_OnScan
    {
        static void Prefix()
        {
            if (!(Plugin.CleanOnScan.Value || Plugin.RemoveAirMarkers.Value || Plugin.SnapMarkers.Value)) return;
            try { Validate(); }
            catch (Exception e) { Plugin.Log.LogError("[OreFix] validation failed: " + e); }
        }

        static void Validate()
        {
            var datas = OreScannerResources.datas;
            var nodes = OctreeNodeResources.OctreeNodeDatas;
            if (!datas.IsCreated || !nodes.IsCreated) return;
            var freeNodes = new HashSet<int>(nodes.IdsToRecycleToArray());
            var freeOre = new HashSet<int>(datas.IdsToRecycleToArray());
            var reachable = new HashSet<int>();
            int air = 0, snapped = 0, orphans = 0, live = 0, cycles = 0;
            var voxels = OctreeNodeResources.voxelNodeDatas;

            for (int nodeId = 1; nodeId < nodes.Length; nodeId++)
            {
                if (freeNodes.Contains(nodeId)) continue;
                OctreeNodeData nd = nodes[nodeId];
                if (nd.oreScannerDataId == 0) continue;
                int root = nd.oreScannerDataId;
                bool changed = false;
                var toRemove = new List<Vector3i>();
                int cur = root, guard = 0;
                while (cur != 0)
                {
                    if (!reachable.Add(cur) || guard++ > 100000) { cycles++; break; }
                    OreScannerData d = datas[cur];
                    Vector3i c = d.coordinate;
                    bool inRange = c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < 20 && c.y < 20 && c.z < 20;
                    if (inRange && nd.densitiesId != 0)
                    {
                        float dens = voxels[nd.densitiesId * 8000 + VoxelNodeData.ToDensityId(new int3(c.x, c.y, c.z))].Density;
                        if (Plugin.RemoveAirMarkers.Value && dens >= 0f) toRemove.Add(c);
                        else if (Plugin.SnapMarkers.Value)
                        {
                            UnityEngine.Vector3 centre = nd.GetPosition(c.x, c.y, c.z);
                            if ((d.localPosition - centre).sqrMagnitude > 1e-4f)
                            {
                                d.localPosition = centre;
                                datas.SetAt(cur, d);
                                snapped++;
                            }
                        }
                    }
                    cur = d.nextElementId;
                }
                foreach (var c in toRemove)
                {
                    int n = Plugin.RemoveFromChain(ref root, c);
                    air += n; changed = true;
                    Plugin.V($"[OreFix] Removed {n} floating marker(s) at voxel {c.x},{c.y},{c.z} of node {nodeId} (voxel is air)");
                }
                if (changed) { nd.oreScannerDataId = root; nodes[nodeId] = nd; }
            }
            // re-collect reachable after removals
            if (Plugin.CleanOnScan.Value)
            {
                for (int i = 1; i < datas.Length; i++)
                {
                    if (freeOre.Contains(i)) continue;
                    var d = datas[i];
                    if (d.transformId == 0) continue;
                    live++;
                    if (!reachable.Contains(i)) { datas.ReleaseId(i); orphans++; }
                }
            }
            Plugin.Log.LogInfo($"[OreFix] Scan validation: entries={live} orphan/ghost released={orphans} air(floating) removed={air} snapped={snapped} brokenChains={cycles}");
        }
    }
}

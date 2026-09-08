using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tas
{
    public struct TasStateEntry
    {
        public long tick;
        public double simTime;
        public uint accumulator;     // TasSim.Accumulator, so the divergence hash rewinds too
        public byte[] data;
        public int len;
    }

    public sealed class TasSlotInfo
    {
        public int slot;
        public long tick;
        public double simTime;
        public uint accumulator;
        public string label = "";
        public string stamp = "";
        public string mapname = "";
        public int branchId = -1;
        public int bytes;
    }

    /// <summary>
    /// Savestates, in two tiers, because they answer two different questions:
    ///
    ///  - RING: one snapshot per tick, in RAM. This is what makes Backspace feel like a TAS
    ///    tool instead of a checkpoint system - you rewind a failed strafe 3 ticks later.
    ///    Cost is real, so it is bounded: ~1.5 KB/tick here, ringCapacity=1800 => ~2.7 MB.
    ///    If that is too fat for your scene, register fewer bodies or raise snapshotEveryTick=false
    ///    and lean on keyframes + re-sim.
    ///  - SLOTS: 10 named snapshots on disk that outlive the session. These are what you
    ///    actually iterate a hard segment against, so they must be shareable/backupable.
    ///
    /// A snapshot is (clock tick, simTime, owned RNG, hash accumulator, registered rigidbodies,
    /// ITasSnapshotable state). Anything in your game that keeps state but is NOT in that list
    /// will survive a rewind as stale data - that is the number-one savestate bug, and the fix
    /// is always "implement ITasSnapshotable on it", never "clear it manually".
    /// </summary>
    public static class TasSavestates
    {
        static TasStateEntry[] ring;
        static byte[][] arena;             // one fixed buffer per slot: zero per-tick allocation
        static int head, count;
        static long lastPushedTick = -1;
        public static int Stride = 8192;
        public static int OverflowCount { get; private set; }
        public static long RingNewestTick { get { return lastPushedTick; } }

        public static int RingCount { get { return count; } }
        public static long RingOldestTick { get { return ring == null || count == 0 ? -1 : ring[head].tick; } }
        public static int SnapshotsTaken { get; private set; }
        public static long LastRollbackTo = -1;

        static readonly Dictionary<int, TasSlotInfo> slots = new Dictionary<int, TasSlotInfo>();
        static readonly List<TasSlotInfo> slotList = new List<TasSlotInfo>();
        public static IReadOnlyList<TasSlotInfo> Slots { get { return slotList; } }

        public static void Init(int capacity)
        {
            capacity = Mathf.Max(2, capacity);
            if (ring != null && ring.Length == capacity) return;      // keep the arena
            ring = new TasStateEntry[capacity];
            arena = new byte[capacity][];
            for (int k = 0; k < capacity; k++) arena[k] = new byte[Stride];
            head = 0; count = 0; lastPushedTick = -1;
        }

        public static long EstimatedRamBytes
        {
            get { return ring == null ? 0 : (long)ring.Length * (Stride + 40); }
        }

        /// <summary>Called from TasTool at the tick tail while the tool is active.</summary>
        public static void Push()
        {
            if (ring == null) Init(TasToolConfig.Load().ringCapacity);
            long t = TasClock.Tick;
            if (t == lastPushedTick) return;         // one per tick, whatever calls us
            lastPushedTick = t;

            int slot = head;
            if (arena[slot] == null || arena[slot].Length != Stride) arena[slot] = new byte[Stride];

            TasStateEntry e = new TasStateEntry();
            e.tick = t;
            e.simTime = TasClock.Exists ? TasClock.i.simTime : 0.0;
            e.accumulator = TasSim.Accumulator;
            e.data = arena[slot];
            e.len = TasSnapshot.CaptureInto(arena[slot]);
            if (e.len < 0)
            {
                OverflowCount++;
                if (OverflowCount == 1)
                    Debug.LogWarning("[TAS] savestate overflow: snapshot > " + Stride +
                                     " B. Raise TasSavestates.Stride or register fewer bodies.");
                return;
            }

            ring[head] = e;
            LastSnapshotBytes = e.len;
            head = (head + 1) % ring.Length;
            if (count < ring.Length) count++;
            SnapshotsTaken++;
        }

        public static bool Has(long tick)
        {
            if (ring == null || count == 0) return false;
            long oldest = ring[(head - count + ring.Length * 2) % ring.Length].tick;
            return tick >= oldest && tick <= lastPushedTick;
        }

        /// <summary>
        /// Re-apply the newest snapshot without touching the clock. This is what a held
        /// (slow-mo / paused) frame is made of: the engine still simulated, so the frame is
        /// undone in LateUpdate and never reaches the screen.
        /// </summary>
        public static bool RestoreNewest()
        {
            if (ring == null || count == 0) return false;
            int idx = (head - 1 + ring.Length * 2) % ring.Length;
            TasStateEntry e = ring[idx];
            if (e.len <= 0) return false;
            TasSnapshot.Restore(e.data, e.len);
            TasSim.Accumulator = e.accumulator;
            return true;
        }

        public static bool TryRollback(long tick, out TasStateEntry entry)
        {
            if (!TryGet(tick, out entry)) return false;
            TasSnapshot.Restore(entry.data, entry.len);
            TasSim.Accumulator = entry.accumulator;
            if (TasClock.Exists) { TasClock.i.tick = entry.tick; TasClock.i.simTime = entry.simTime; }
            LastRollbackTo = tick;
            return true;
        }

        /// <summary>Snapshot size actually used, for tuning Stride.</summary>
        public static int LastSnapshotBytes { get; private set; }

        public static bool TryGet(long tick, out TasStateEntry e)
        {
            e = default(TasStateEntry);
            if (ring == null || count == 0) return false;
            for (int k = 1; k <= count; k++)
            {
                int idx = (head - k + ring.Length * 2) % ring.Length;
                if (ring[idx].tick == tick) { e = ring[idx]; return true; }
                if (ring[idx].tick < tick) return false;      // ring is ordered, stop early
            }
            return false;
        }



        // ---- slots ----
        static string SlotPath(int i) { return Path.Combine(TasToolConfig.Dir, "slot_" + i + ".tasst"); }
        static string MetaPath { get { return Path.Combine(TasToolConfig.Dir, "slots.json"); } }

        public static int SaveSlot(int i, string label, int branchId)
        {
            TasSlotInfo s = new TasSlotInfo();
            s.slot = i;
            s.tick = TasClock.Tick;
            s.simTime = TasClock.Exists ? TasClock.i.simTime : 0.0;
            s.accumulator = TasSim.Accumulator;
            s.label = string.IsNullOrEmpty(label) ? ("slot " + i) : label;
            s.stamp = DateTime.Now.ToString("HH:mm:ss");
            s.branchId = branchId;
            s.mapname = TasRecorder.i != null && TasRecorder.i.Tape != null ? TasRecorder.i.Tape.header.mapname : "";

            byte[] data = TasSnapshot.Capture();
            s.bytes = data == null ? 0 : data.Length;
            try
            {
                using (FileStream fs = new FileStream(SlotPath(i), FileMode.Create, FileAccess.Write))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write(s.tick); w.Write(s.simTime); w.Write(s.accumulator);
                    w.Write(i); w.Write(data.Length); w.Write(data);
                }
                slots[i] = s;
                RebuildList();
                WriteMeta();
                Debug.Log("[TAS] slot " + i + " saved @tick " + s.tick + " (" + data.Length + " B)");
            }
            catch (Exception e)
            {
                Debug.LogError("[TAS] slot save failed: " + e.Message);
                return -1;
            }
            return i;
        }

        public static TasSlotInfo LoadSlot(int i)
        {
            string p = SlotPath(i);
            if (!File.Exists(p)) { Debug.LogWarning("[TAS] slot " + i + " empty"); return null; }
            try
            {
                long tick; double simTime; uint acc;
                using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    tick = r.ReadInt64();
                    simTime = r.ReadDouble();
                    acc = r.ReadUInt32();
                    int slot = r.ReadInt32();
                    byte[] data = r.ReadBytes(r.ReadInt32());
                    TasSnapshot.Restore(data);
                    TasSim.Accumulator = acc;
                    if (TasClock.Exists) { TasClock.i.tick = tick; TasClock.i.simTime = simTime; }
                    LastRollbackTo = tick;
                }
                TasSlotInfo info;
                if (slots.TryGetValue(i, out info)) Debug.Log("[TAS] slot " + i + " loaded @tick " + tick);
                return info;
            }
            catch (Exception e)
            {
                Debug.LogError("[TAS] slot load failed: " + e.Message);
                return null;
            }
        }

        public static bool SlotExists(int i) { return File.Exists(SlotPath(i)); }

        public static TasSlotInfo SlotInfo(int i)
        {
            TasSlotInfo s;
            return slots.TryGetValue(i, out s) ? s : null;
        }

        static void RebuildList()
        {
            slotList.Clear();
            for (int k = 0; k < 32; k++)
            {
                TasSlotInfo s;
                if (slots.TryGetValue(k, out s)) slotList.Add(s);
            }
        }

        static void WriteMeta()
        {
            try
            {
                var sb = new System.Text.StringBuilder("[");
                for (int k = 0; k < slotList.Count; k++)
                {
                    TasSlotInfo s = slotList[k];
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"slot\":").Append(s.slot)
                      .Append(",\"tick\":").Append(s.tick)
                      .Append(",\"simTime\":").Append(s.simTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"accumulator\":").Append(s.accumulator)
                      .Append(",\"branchId\":").Append(s.branchId)
                      .Append(",\"bytes\":").Append(s.bytes)
                      .Append(",\"label\":\"").Append(Escape(s.label)).Append("\"")
                      .Append(",\"stamp\":\"").Append(Escape(s.stamp)).Append("\"")
                      .Append(",\"mapname\":\"").Append(Escape(s.mapname)).Append("\"}");
                }
                sb.Append(']');
                File.WriteAllText(MetaPath, sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[TAS] slot meta: " + e.Message); }
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>Read slot metadata written by any session (the panel lists these before you load one).</summary>
        public static void RefreshSlotMeta()
        {
            try
            {
                if (!File.Exists(MetaPath)) return;
                string json = File.ReadAllText(MetaPath);
                SlotMetaFile f = JsonUtility.FromJson<SlotMetaFile>("{\"items\":" + json + "}");
                if (f == null || f.items == null) return;
                slotList.Clear();
                slots.Clear();
                for (int k = 0; k < f.items.Length; k++)
                {
                    TasSlotInfo s = f.items[k];
                    slots[s.slot] = s; slotList.Add(s);
                }
            }
            catch (Exception e) { Debug.LogWarning("[TAS] slot meta: " + e.Message); }
        }

        [Serializable] class SlotMetaFile { public TasSlotInfo[] items; }

        public static void ClearRing()
        {
            if (ring != null) for (int k = 0; k < ring.Length; k++) ring[k] = default(TasStateEntry);
            head = 0; count = 0; lastPushedTick = -1;
            SnapshotsTaken = 0;
        }
    }

    /// <summary>
    /// Visual clutter that is NOT simulation but looks broken after a rewind (decals, trails,
    /// trails renderers, spawned hit effects, damage numbers). Rewinding the sim while blood
    /// stays on the wall makes people report "savestate is buggy" when it is not.
    /// </summary>
    public static class TasVisualReset
    {
        public static void Reset()
        {
            try
            {
                ParticleSystem[] all = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
                for (int k = 0; k < all.Length; k++)
                {
                    if (all[k] == null) continue;
                    if (all[k].main.simulationSpace == ParticleSystemSimulationSpace.World)
                    { all[k].Clear(); if (all[k].isPlaying) all[k].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
                }
                TrailRenderer[] trails = UnityEngine.Object.FindObjectsOfType<TrailRenderer>();
                for (int k = 0; k < trails.Length; k++) if (trails[k] != null) trails[k].Clear();
            }
            catch (Exception e) { Debug.LogWarning("[TAS] visual reset: " + e.Message); }
        }
    }
}

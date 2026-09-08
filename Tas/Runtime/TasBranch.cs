using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// An attempt that was rewound past.
    ///
    /// This is the answer to "if I used savestates it's gonna replay everything as if it was
    /// all one run". A TAS run is not a recording of your session - your session contains the
    /// retries. It is ONE CONTIGUOUS TICK RANGE: trunk = trunk[0..T] ++ tail_of_chosen_branch.
    /// Because the trunk stays contiguous (no gaps, no "attempt 3 started here" markers, no
    /// re-seeded RNG), realtime replay of it needs no special casing at all - it is just the
    /// run. Rollback therefore must do three things atomically: restore the sim, truncate the
    /// trunk, and stash the discarded tail as a branch so nothing is ever lost.
    /// </summary>
    [Serializable]
    public class TasBranch
    {
        public int id;
        public string label = "";
        public int parentTick;                 // trunk length when this branch was cut
        public long createdAtTick;
        public double durationSeconds;
        public uint accumulatorAtCut;          // TasSim.Accumulator at parentTick (hash chain must restart there)
        public uint accumulatorAtEnd;
        public int frameCount;                 // mirror for fast listing
        public bool discarded;                 // true = was replaced by a later attempt
        public List<TasInputFrame> frames = new List<TasInputFrame>();
        public List<TasCheckpoint> checkpoints = new List<TasCheckpoint>();

        public void Adopt(IReadOnlyList<TasInputFrame> f, int from, IReadOnlyList<TasCheckpoint> c, int fromC)
        {
            frames.Clear();
            int n = f.Count;
            for (int k = from; k < n; k++) frames.Add(f[k]);
            checkpoints.Clear();
            int m = c.Count;
            for (int k = fromC; k < m; k++) checkpoints.Add(c[k]);
            frameCount = frames.Count;
            durationSeconds = frameCount * (1.0 / Mathf.Max(1, TasClock.Rate));
            accumulatorAtEnd = TasSim.Accumulator;
        }
    }

    public sealed class TasBranchStore
    {
        readonly List<TasBranch> list = new List<TasBranch>(8);
        int nextId = 1;
        public IReadOnlyList<TasBranch> Branches { get { return list; } }
        public int Count { get { return list.Count; } }

        public event Action OnChanged;

        public void Clear() { list.Clear(); nextId = 1; if (OnChanged != null) OnChanged(); }

        /// <summary>
        /// Stash trunk[atTick..] as a branch BEFORE the trunk is truncated, so the discarded
        /// attempt is still selectable. fromC walks the checkpoint list to the first entry >= atTick.
        /// </summary>
        public TasBranch Cut(int atTick, uint accumulatorAtTick, string label)
        {
            TasRecorder r = TasRecorder.i;
            if (r == null || !r.CanBranch || atTick >= r.TickCount) return null;

            TasBranch b = new TasBranch();
            b.id = nextId++;
            b.label = string.IsNullOrEmpty(label) ? ("attempt " + b.id) : label;
            b.parentTick = atTick;
            b.createdAtTick = TasClock.Tick;
            b.accumulatorAtCut = accumulatorAtTick;

            int fromC = 0;
            IReadOnlyList<TasCheckpoint> cks = r.CheckpointList;
            while (fromC < cks.Count && cks[fromC].tick < atTick) fromC++;

            b.Adopt(r.Frames, atTick, cks, fromC);
            if (b.frames.Count == 0) return null;

            list.Add(b);
            while (list.Count > TasToolConfig.Load().maxBranches)
            {
                int worst = -1; double best = double.MaxValue;
                for (int k = 0; k < list.Count; k++)
                {
                    if (list[k].durationSeconds < best) { best = list[k].durationSeconds; worst = k; }
                }
                if (worst < 0) break;
                list.RemoveAt(worst);
            }
            if (OnChanged != null) OnChanged();
            return b;
        }

        public TasBranch Find(int id)
        {
            for (int k = 0; k < list.Count; k++) if (list[k].id == id) return list[k];
            return null;
        }

        public void Drop(int id)
        {
            TasBranch b = Find(id);
            if (b != null) { list.Remove(b); if (OnChanged != null) OnChanged(); }
        }

        /// <summary>
        /// Replace trunk[parentTick..] with this branch's tail. After a splice the trunk is again
        /// one contiguous range, so the replay still needs no special handling - and because the
        /// branch's checkpoints were hashed from accumulatorAtCut, the divergence chain stays valid.
        /// </summary>
        public bool Splice(int id)
        {
            TasBranch b = Find(id);
            TasRecorder r = TasRecorder.i;
            if (b == null || r == null) return false;
            if (b.parentTick > r.TickCount)
            {
                // the trunk got rewound below where this branch starts; rewind the sim there first
                TasStateEntry e;
                if (!TasSavestates.TryRollback(b.parentTick, out e))
                {
                    Debug.LogWarning("[TAS] cannot splice branch " + id + ": tick " + b.parentTick +
                                     " is out of both trunk and ring range");
                    return false;
                }
            }
            bool ok = r.SpliceTail(b.parentTick, b.frames, b.checkpoints, b.accumulatorAtCut);
            if (ok)
            {
                b.discarded = false;
                Debug.Log("[TAS] spliced branch " + id + " (" + b.label + ") at tick " + b.parentTick +
                          " -> trunk now " + r.TickCount + " ticks");
                if (OnChanged != null) OnChanged();
            }
            return ok;
        }

        public void MarkDiscardedAfter(int parentTick)
        {
            for (int k = 0; k < list.Count; k++)
                if (list[k].parentTick >= parentTick) list[k].discarded = true;
        }

        // ---- persistence: one file per run, so a session survives a crash ----
        public void Write(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write(list.Count);
                    for (int k = 0; k < list.Count; k++)
                    {
                        TasBranch b = list[k];
                        w.Write(b.id); w.Write(b.parentTick); w.Write(b.createdAtTick);
                        w.Write(b.durationSeconds); w.Write(b.accumulatorAtCut); w.Write(b.accumulatorAtEnd);
                        w.Write(b.discarded);
                        w.Write(b.label ?? "");
                        w.Write(b.frames.Count);
                        for (int i = 0; i < b.frames.Count; i++) b.frames[i].Write(w);
                        w.Write(b.checkpoints.Count);
                        for (int i = 0; i < b.checkpoints.Count; i++) b.checkpoints[i].Write(w);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[TAS] branch write: " + e.Message); }
        }

        public void Read(string path)
        {
            if (!File.Exists(path)) return;
            list.Clear(); nextId = 1;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        TasBranch b = new TasBranch();
                        b.id = r.ReadInt32(); b.parentTick = r.ReadInt32(); b.createdAtTick = r.ReadInt64();
                        b.durationSeconds = r.ReadDouble(); b.accumulatorAtCut = r.ReadUInt32();
                        b.accumulatorAtEnd = r.ReadUInt32(); b.discarded = r.ReadBoolean();
                        b.label = r.ReadString();
                        int fn = r.ReadInt32();
                        for (int i = 0; i < fn; i++) b.frames.Add(TasInputFrame.Read(r));
                        int cn = r.ReadInt32();
                        for (int i = 0; i < cn; i++) b.checkpoints.Add(TasCheckpoint.Read(r));
                        b.frameCount = b.frames.Count;
                        list.Add(b);
                        if (b.id >= nextId) nextId = b.id + 1;
                    }
                }
                if (OnChanged != null) OnChanged();
            }
            catch (Exception e) { Debug.LogWarning("[TAS] branch read: " + e.Message); }
        }

        public string Summary(int id)
        {
            TasBranch b = Find(id);
            if (b == null) return "-";
            return string.Format("#{0} {1}  from tick {2}  {3} ticks  {4:0.000}s{5}",
                b.id, b.label, b.parentTick, b.frameCount, b.durationSeconds, b.discarded ? "  (superseded)" : "");
        }
    }
}

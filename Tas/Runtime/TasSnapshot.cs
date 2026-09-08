using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Sim-wide state that a snapshot must own. Anything you cannot reconstruct from inputs
    /// belongs here, and anything you put here MUST be restored on rollback, or your
    /// "TAS from frame N" silently diverges.
    /// </summary>
    public static class TasSim
    {
        public static TasRng Rng = TasRng.FromNow();
        public static uint Accumulator;          // rolling hash the checkpoints compare against
        public static long TickAtSnapshot = -1;
        public static double SimTimeAtSnapshot;

        /// <summary>Gameplay RNG entry point. Replaces Random.value / Random.Range.</summary>
        public static float value { get { return TasClock.Exists ? (Rng.Next01()) : UnityEngine.Random.value; } }
        public static int Range(int a, int b) { return TasClock.Exists ? Rng.Range(a, b) : UnityEngine.Random.Range(a, b); }
    }

    /// <summary>Implement on every component whose state is not derivable from transforms+velocities.</summary>
    public interface ITasSnapshotable
    {
        void TasSave(BinaryWriter w);
        void TasLoad(BinaryReader r);
    }

    /// <summary>
    /// Savestates. This is the one thing an emulator gives a TAS tool for free (the whole
    /// machine is a byte array) and Unity does not: there is no single "state" to snapshot,
    /// so you assemble one from (a) registered rigidbodies, (b) ITasSnapshotable components,
    /// (c) clock + owned RNG. Without this you cannot have rollback or scrub-to-any-tick,
    /// and without rollback it is not a TAS tool, it is a video.
    /// </summary>
    public static class TasSnapshot
    {
        class Body { public Transform t; public Rigidbody rb; }

        static readonly List<Body> bodies = new List<Body>(16);
        static readonly List<ITasSnapshotable> extras = new List<ITasSnapshotable>(16);
        static readonly Dictionary<long, byte[]> byTick = new Dictionary<long, byte[]>(256);
        static readonly Queue<long> lru = new Queue<long>(256);
        const int CacheCap = 512;

        public static int Count { get { return byTick.Count; } }

        public static void Register(Transform t, Rigidbody rb)
        {
            if (t == null) return;
            for (int k = 0; k < bodies.Count; k++) if (bodies[k].t == t) return;
            bodies.Add(new Body { t = t, rb = rb });
        }

        public static void Register(ITasSnapshotable s)
        {
            if (s == null) return;
            if (!extras.Contains(s)) extras.Add(s);
        }

        public static void Clear() { bodies.Clear(); extras.Clear(); byTick.Clear(); lru.Clear(); }

        public static byte[] Capture()
        {
            using (MemoryStream ms = new MemoryStream(1024))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(TasClock.Tick);
                w.Write(TasClock.Exists ? TasClock.i.simTime : 0.0);
                w.Write(TasSim.Rng.s);
                w.Write(TasSim.Accumulator);

                w.Write(bodies.Count);
                for (int k = 0; k < bodies.Count; k++)
                {
                    TasRigidbodyState s = default(TasRigidbodyState);
                    Body b = bodies[k];
                    if (b.t != null)
                        TasPhysics.Capture(ref b.rb, ref b.t, ref s);
                    w.Write(s.pos.x); w.Write(s.pos.y); w.Write(s.pos.z);
                    w.Write(s.rot.x); w.Write(s.rot.y); w.Write(s.rot.z); w.Write(s.rot.w);
                    w.Write(s.vel.x); w.Write(s.vel.y); w.Write(s.vel.z);
                    w.Write(s.angVel.x); w.Write(s.angVel.y); w.Write(s.angVel.z);
                    w.Write(s.kinematic);
                }

                w.Write(extras.Count);
                for (int k = 0; k < extras.Count; k++)
                    if (extras[k] != null) extras[k].TasSave(w);

                w.Flush();
                return ms.ToArray();
            }
        }

        public static void Restore(byte[] data)
        {
            if (data == null) return;
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader r = new BinaryReader(ms))
            {
                long tick = r.ReadInt64();
                double simTime = r.ReadDouble();
                TasSim.Rng = new TasRng(r.ReadUInt64());
                TasSim.Accumulator = r.ReadUInt32();
                if (TasClock.Exists) { TasClock.i.tick = tick; TasClock.i.simTime = simTime; }

                int n = r.ReadInt32();
                for (int k = 0; k < n; k++)
                {
                    TasRigidbodyState s = default(TasRigidbodyState);
                    s.pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.angVel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.kinematic = r.ReadBoolean();
                    if (k < bodies.Count)
                    {
                        Body b = bodies[k];
                        if (b.t != null) TasPhysics.Restore(ref b.rb, ref b.t, ref s);
                    }
                }

                int m = r.ReadInt32();
                for (int k = 0; k < m; k++)
                    if (k < extras.Count && extras[k] != null) extras[k].TasLoad(r);

                Physics.SyncTransforms();
            }
        }

        public static long PutAtCurrentTick()
        {
            long t = TasClock.Tick;
            byTick[t] = Capture();
            lru.Enqueue(t);
            while (lru.Count > CacheCap) byTick.Remove(lru.Dequeue());
            return t;
        }

        /// <summary>Used by playback SeekTo: exact if we still hold that keyframe, else best-effort no-op.</summary>
        public static bool RestoreIfAvailable(long tick)
        {
            byte[] d;
            if (byTick.TryGetValue(tick, out d)) { Restore(d); return true; }
            Debug.LogWarning("[TAS] no keyframe for tick " + tick + ", re-simming from 0 instead");
            return false;
        }

        // ---- sidecar file so tool sessions survive a domain reload / editor restart ----
        public static void WriteSidecar(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(byTick.Count);
                foreach (KeyValuePair<long, byte[]> kv in byTick) { w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); }
            }
        }

        public static void ReadSidecar(string path)
        {
            if (!File.Exists(path)) return;
            byTick.Clear(); lru.Clear();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader r = new BinaryReader(fs))
            {
                int n = r.ReadInt32();
                for (int k = 0; k < n; k++)
                {
                    long t = r.ReadInt64();
                    byte[] d = r.ReadBytes(r.ReadInt32());
                    byTick[t] = d; lru.Enqueue(t);
                }
            }
        }
    }
}

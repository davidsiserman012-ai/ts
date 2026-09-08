using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Sparse state frame, recorded at checkpointInterval ticks. This is the old
    /// DemoFrame, kept ONLY as a checksum + correction anchor, never as the replay source.
    /// </summary>
    public struct TasCheckpoint
    {
        public int tick;
        public float px, py, pz;
        public float camPitch, bodyYaw;
        public float speed;
        public uint stateHash;     // FNV-1a over the sim state that produced this tick

        public Vector3 Position { get { return new Vector3(px, py, pz); } set { px = value.x; py = value.y; pz = value.z; } }

        public void Write(BinaryWriter w)
        {
            w.Write(tick); w.Write(px); w.Write(py); w.Write(pz);
            w.Write(camPitch); w.Write(bodyYaw); w.Write(speed); w.Write(stateHash);
        }

        public static TasCheckpoint Read(BinaryReader r)
        {
            TasCheckpoint c = new TasCheckpoint();
            c.tick = r.ReadInt32();
            c.px = r.ReadSingle(); c.py = r.ReadSingle(); c.pz = r.ReadSingle();
            c.camPitch = r.ReadSingle(); c.bodyYaw = r.ReadSingle();
            c.speed = r.ReadSingle(); c.stateHash = r.ReadUInt32();
            return c;
        }
    }

    [Serializable]
    public class TasTapeHeader
    {
        public int version = TasTape.FormatVersion;
        public int tickRate = 60;
        public int tickCount;
        public int checkpointEvery = 300;
        public uint mapHash;
        public uint configHash;         // the anti-cheat anchor: physics + sens + gravity + stats
        public ulong rngSeed;
        public string gameVersion;
        public string nick;
        public int flagId, avatarId, knifeId, capeId, gloveId, effectId, rankIdx;
        public string controlTypeStr;
        public int controlType;
        public string mapname;
        public string rankStr;
        public int finishTick = -1;         // where the level actually completed; tape may run past it
        public string finishReason;         // "level" | "death" | "maxTicks" | "manual"
        public int gameRunSeconds;          // GameManager's TimeManager.Seconds, for cross-checking
        public double runSeconds;
    }

    /// <summary>
    /// Binary run tape: header + input frames + checkpoints.
    ///
    /// Difference from DemoData that matters commercially: a state tape (positions) is
    /// FORGEABLE BY CONSTRUCTION - whoever writes the JSON decides where the player was.
    /// An input tape has to be *re-simulated* to produce a time, so the same tape replayed
    /// on the verifying machine either yields the claimed splits or it does not. You go
    /// from "trust the client" to "reproduce the client", which is the whole ballgame for
    /// a leaderboard.
    /// </summary>
    public sealed class TasTape
    {
        public const int FormatVersion = 2;   // v2 = 32-byte frames (4 aux analog channels)
        const uint Magic = 0x54534154;   // "TAST" little-endian

        public TasTapeHeader header = new TasTapeHeader();
        public List<TasInputFrame> frames = new List<TasInputFrame>(8192);
        public List<TasCheckpoint> checkpoints = new List<TasCheckpoint>(64);
        public string metaJson;
        public string SavedPath;

        // ---- streaming write: no big managed List peak, no GC spikes mid-run ----
        public static void Write(string path, TasTape tape)
        {
            string tmp = path + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
            {
                WriteTo(w, tape);
                w.Flush();
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            tape.SavedPath = path;
        }

        public static void WriteTo(BinaryWriter w, TasTape tape)
        {
            TasTapeHeader h = tape.header;
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(h.tickRate);
            w.Write(h.tickCount);
            w.Write(h.checkpointEvery);
            w.Write(h.mapHash);
            w.Write(h.configHash);
            w.Write(h.rngSeed);

            WriteStr(w, h.gameVersion);
            WriteStr(w, h.nick);
            WriteStr(w, h.mapname);
            WriteStr(w, h.controlTypeStr);
            WriteStr(w, h.rankStr);
            WriteStr(w, tape.metaJson);

            w.Write(h.flagId); w.Write(h.avatarId); w.Write(h.knifeId);
            w.Write(h.capeId); w.Write(h.gloveId); w.Write(h.effectId);
            w.Write(h.rankIdx); w.Write(h.controlType);
            w.Write(h.finishTick); w.Write(h.gameRunSeconds);
            WriteStr(w, h.finishReason);
            w.Write(h.runSeconds);

            w.Write(tape.frames.Count);
            for (int k = 0; k < tape.frames.Count; k++) tape.frames[k].Write(w);

            w.Write(tape.checkpoints.Count);
            for (int k = 0; k < tape.checkpoints.Count; k++) tape.checkpoints[k].Write(w);

            // Integrity is defined over the *fields*, not the encoding, so a verifier can
            // recompute it without parsing, and no seek/rewrite dance is needed here.
            w.Write(FramesHash(tape.frames));
        }

        public static uint FramesHash(List<TasInputFrame> frames)
        {
            uint h = 2166136261u;
            for (int k = 0; k < frames.Count; k++)
            {
                TasInputFrame f = frames[k];
                h = TasRng.Mix(h, (int)f.buttons);
                h = TasRng.Mix(h, (int)((f.moveX << 16) | (f.moveY & 0xFFFF)));
                h = TasRng.Mix(h, f.lookX);
                h = TasRng.Mix(h, f.lookY);
                h = TasRng.Mix(h, (int)((f.aimX << 16) | (f.aimY & 0xFFFF)));
                h = TasRng.Mix(h, (int)f.weaponSlot | ((int)f.flags << 8));
            }
            return h;
        }

        public static TasTape Read(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                return ReadFrom(new BinaryReader(fs, Encoding.UTF8));
        }

        public static TasTape ReadFrom(BinaryReader r)
        {
            TasTape tape = new TasTape();
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a .tas tape");

            int ver = r.ReadInt32();
            if (ver > FormatVersion) throw new InvalidDataException("tape version " + ver + " newer than reader " + FormatVersion);
            bool legacy = ver < 2;

            TasTapeHeader h = tape.header;
            h.version = ver;
            h.tickRate = r.ReadInt32();
            h.tickCount = r.ReadInt32();
            h.checkpointEvery = r.ReadInt32();
            h.mapHash = r.ReadUInt32();
            h.configHash = r.ReadUInt32();
            h.rngSeed = r.ReadUInt64();
            h.gameVersion = ReadStr(r);
            h.nick = ReadStr(r);
            h.mapname = ReadStr(r);
            h.controlTypeStr = ReadStr(r);
            h.rankStr = ReadStr(r);
            tape.metaJson = ReadStr(r);
            h.flagId = r.ReadInt32(); h.avatarId = r.ReadInt32(); h.knifeId = r.ReadInt32();
            h.capeId = r.ReadInt32(); h.gloveId = r.ReadInt32(); h.effectId = r.ReadInt32();
            h.rankIdx = r.ReadInt32(); h.controlType = r.ReadInt32();
            if (!legacy)
            {
                h.finishTick = r.ReadInt32(); h.gameRunSeconds = r.ReadInt32();
                h.finishReason = ReadStr(r);
            }
            h.runSeconds = r.ReadDouble();

            int n = r.ReadInt32();
            tape.frames.Capacity = n;
            for (int k = 0; k < n; k++) tape.frames.Add(legacy ? TasInputFrame.ReadLegacyV1(r) : TasInputFrame.Read(r));

            int cn = r.ReadInt32();
            for (int k = 0; k < cn; k++) tape.checkpoints.Add(TasCheckpoint.Read(r));
            r.ReadUInt32();      // frame block hash: validated by the verifier, not here
            return tape;
        }

        static void WriteStr(BinaryWriter w, string s)
        {
            if (s == null) { w.Write(0); return; }
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }

        static string ReadStr(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len <= 0) return string.Empty;
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        public static uint Fnv(byte[] data)
        {
            uint h = 2166136261u;
            for (int k = 0; k < data.Length; k++) { h ^= data[k]; h *= 16777619u; }
            return h;
        }

        public static uint HashOf(object o)
        {
            return Fnv(Encoding.UTF8.GetBytes(o == null ? "" : o.ToString()));
        }

        public double SecondsAt(int tick) { return tick * (1.0 / Mathf.Max(1, header.tickRate)); }
        public double DurationSeconds { get { return SecondsAt(header.tickCount); } }

        public int NearestCheckpointBefore(int tick)
        {
            int lo = 0, hi = checkpoints.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (checkpoints[mid].tick <= tick) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }

        /// <summary>Interpolated state at an arbitrary render time (the "demo" view).</summary>
        public TasCheckpoint SampleState(double tickF)
        {
            if (checkpoints.Count == 0) return default(TasCheckpoint);
            int i0 = Mathf.Clamp((int)Mathf.Floor((float)(tickF / Mathf.Max(1, header.checkpointEvery))), 0, checkpoints.Count - 1);
            int i1 = Mathf.Min(i0 + 1, checkpoints.Count - 1);
            TasCheckpoint a = checkpoints[i0], b = checkpoints[i1];
            float span = Mathf.Max(1, b.tick - a.tick);
            float t = Mathf.Clamp01((float)((tickF - a.tick) / span));
            if (i0 == i1) return a;
            TasCheckpoint o = a;
            o.Position = Vector3.Lerp(a.Position, b.Position, t);
            o.camPitch = Mathf.Lerp(a.camPitch, b.camPitch, t);
            o.bodyYaw = Mathf.LerpUnclamped(a.bodyYaw, b.bodyYaw, t);
            o.speed = Mathf.Lerp(a.speed, b.speed, t);
            return o;
        }

        public void BuildIndex()
        {
            header.tickCount = frames.Count;
            header.runSeconds = SecondsAt(frames.Count);
        }

        public string Summary()
        {
            return string.Format("[TAS] {0} {1} ticks @{2}Hz = {3:0.000}s  map={4} cfg={5:X8} ckpt={6} bytes={7}",
                header.nick, header.tickCount, header.tickRate, header.runSeconds, header.mapname,
                header.configHash, checkpoints.Count, header.tickCount * TasInputFrame.Size);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Mirror of your existing DemoData/DemoFrame JSON shape, generated FROM the tape.
    /// Why it exists: your leaderboard player, your upload endpoint and your CDN path do not
    /// have to change at all. The TAS tool becomes the recorder; the demo format becomes a
    /// lossy view of it. That is the cheapest possible migration and it keeps both worlds:
    /// tape for verification/authoring, JSON for the existing render path.
    /// ⚠ field names must match your DemoData/DemoFrame exactly, or JsonUtility silently
    ///   produces an empty object. Rename to match, do not rename these.
    /// </summary>
    [Serializable]
    public class TasDemoFrameCompat
    {
        public float t;
        public float x, y, z;
        public float camPitch;     // your gp.SetData(demoTime, pos.x,pos.y,pos.z, camEuler.x, playerEuler.y, speed)
        public float bodyYaw;
        public int speed;
    }

    [Serializable]
    public class TasDemoCompat
    {
        public string mapname;
        public string nick;
        public int flagId, avatarId, knifeId, capeId, gloveId, effectId;
        public string rankStr;
        public int rankIdx;
        public List<TasDemoFrameCompat> frames = new List<TasDemoFrameCompat>();
        public int time;
        public int screenWidth, screenHeight;
        public string controlTypeStr;
        public int controlType;
        public string appVer;
    }

    public static class TasDemoExport
    {
        public static TasDemoCompat ToDemo(TasTape tape)
        {
            TasDemoCompat d = new TasDemoCompat();
            TasTapeHeader h = tape.header;
            d.mapname = h.mapname;
            d.nick = h.nick;
            d.flagId = h.flagId; d.avatarId = h.avatarId; d.knifeId = h.knifeId;
            d.capeId = h.capeId; d.gloveId = h.gloveId; d.effectId = h.effectId;
            d.rankIdx = h.rankIdx;
            d.rankStr = h.rankStr;
            d.controlTypeStr = h.controlTypeStr; d.controlType = h.controlType;
            d.appVer = h.gameVersion;
            d.screenWidth = Screen.width; d.screenHeight = Screen.height;

            // one demo frame per checkpoint: 12Hz state at 60Hz/5 + full-rate input, so the existing
            // interpolating player keeps working while the tape retains everything.
            for (int k = 0; k < tape.checkpoints.Count; k++)
            {
                TasCheckpoint c = tape.checkpoints[k];
                d.frames.Add(new TasDemoFrameCompat
                {
                    t = c.tick / (float)Mathf.Max(1, h.tickRate),
                    x = c.px, y = c.py, z = c.pz,
                    camPitch = c.camPitch,
                    bodyYaw = c.bodyYaw,
                    speed = Mathf.RoundToInt(c.speed),
                });
            }
            d.time = d.frames.Count;
            return d;
        }

        public static string ToJson(TasTape tape) { return JsonUtility.ToJson(ToDemo(tape)); }

        // ---- transport: 864 KB tape -> ~60-120 KB payload ----
        public static byte[] Gzip(byte[] data)
        {
            using (MemoryStream o = new MemoryStream(Mathf.Max(256, data.Length / 8)))
            {
                using (GZipStream g = new GZipStream(o, CompressionMode.Compress, true)) g.Write(data, 0, data.Length);
                return o.ToArray();
            }
        }

        public static byte[] Gunzip(byte[] data)
        {
            using (MemoryStream i = new MemoryStream(data))
            using (GZipStream g = new GZipStream(i, CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream(Mathf.Max(256, data.Length * 8)))
            {
                byte[] buf = new byte[16384];
                int n;
                while ((n = g.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                return o.ToArray();
            }
        }

        public static string ToWire(TasTape tape) { return Convert.ToBase64String(Gzip(Serialize(tape))); }
        public static TasTape FromWire(string b64) { return Deserialize(Gunzip(Convert.FromBase64String(b64))); }

        public static byte[] Serialize(TasTape tape)
        {
            using (MemoryStream ms = new MemoryStream(65536))
            {
                using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8)) TasTape.WriteTo(w, tape);
                return ms.ToArray();
            }
        }

        public static TasTape Deserialize(byte[] bytes)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
                return TasTape.ReadFrom(new BinaryReader(ms, Encoding.UTF8));
        }

        /// <summary>
        /// Body for your leaderboard POST. Send BOTH: the tape (authoritative, verifiable)
        /// and the derived demo JSON (so the existing viewer needs no change). Time reported to
        /// the server must come from the tape, never from a client-side stopwatch.
        /// </summary>
        public static string BuildUploadPayload(TasTape tape)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"map\":\"").Append(tape.header.mapname)
              .Append("\",\"nick\":\"").Append(tape.header.nick)
              .Append("\",\"ticks\":").Append(tape.header.tickCount)
              .Append(",\"tickRate\":").Append(tape.header.tickRate)
              .Append(",\"seconds\":").Append(tape.header.runSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"configHash\":\"").Append(tape.header.configHash.ToString("X8"))
              .Append("\",\"mapHash\":\"").Append(tape.header.mapHash.ToString("X8"))
              .Append("\",\"seed\":\"").Append(tape.header.rngSeed)
              .Append("\",\"tape\":\"").Append(ToWire(tape))
              .Append("\",\"demo\":").Append(ToJson(tape))
              .Append("}");
            return sb.ToString();
        }
    }
}

using System.IO;
using UnityEngine;

namespace Tas
{
    [System.Flags]
    public enum TasButton : uint
    {
        None       = 0u,
        Jump       = 1u << 0,
        Fire       = 1u << 1,
        AltFire    = 1u << 2,
        Reload     = 1u << 3,
        Use        = 1u << 4,
        Crouch     = 1u << 5,
        Walk       = 1u << 6,
        Sprint     = 1u << 7,
        Slot1      = 1u << 8,
        Slot2      = 1u << 9,
        Slot3      = 1u << 10,
        Slot4      = 1u << 11,
        Slot5      = 1u << 12,
        JumpPressed= 1u << 13,   // rising edge, kept in the tape so playback is frame-exact
        FirePressed= 1u << 14,
        AimDown    = 1u << 15,
        Buy        = 1u << 16,

        // Your InputData has no axis for movement: KeyboardSupport/PointerDown set six booleans and
        // CalculateDirection() turns them into input.direction. Those six ARE the movement input on
        // PC, so they belong in the tape as held bits (edge events would be lost - see the tap note).
        DirLeft    = 1u << 17,
        DirRight   = 1u << 18,
        DirFwd     = 1u << 19,
        DirBck     = 1u << 20,
        DirUp      = 1u << 21,
        DirDown    = 1u << 22,
    }

    /// <summary>
    /// One tick of player input, packed, fixed-point, 24 bytes.
    ///
    /// Three deliberate choices:
    ///  1. HELD STATE (bitmask), not key-down events. `GetKeyDown`-style reads depend on
    ///     when the OS happened to deliver the event relative to the tick, so events cannot
    ///     be replayed. A bitmask per tick is replayable by construction.
    ///  2. FIXED-POINT (x1000) for movement, and screen-space normalized u16 for pointers.
    ///     Floats recorded as text (JsonUtility) lose digits; u16 normalized pointers survive
    ///     any resolution, which is why your screenWidth/screenHeight fields disappear.
    ///  3. FIXED-SIZE records. No RLE, no varint: O(1) seek to any tick is what makes
    ///     scrubbing / frame-advance / rollback instant. 36000 ticks = 864 KB raw,
    ///     ~40x smaller than 36000 JSON DemoFrames.
    /// </summary>
    public struct TasInputFrame
    {
        public const int FP = 1000;              // fixed-point scale
        public const ushort PointerAbsent = ushort.MaxValue;
        public const int Size = 32;              // v2: 24 + four analog aux channels; keep in sync with Write/Read

        public uint buttons;
        public short moveX;                      // [-FP..FP]
        public short moveY;                      // [-FP..FP]
        public int lookX;                        // degrees applied THIS tick, xFP
        public int lookY;                        // degrees applied THIS tick, xFP
        public ushort aimX;                      // normalized screen pos of aim pointer, 0..65535
        public ushort aimY;
        public byte pointerCount;                // total touches (for multi-touch control schemes)
        public byte weaponSlot;                  // 1..5, informational + checksum
        public byte flags;                       // TasFrameFlags
        public byte pad;                         // explicit padding -> stable binary layout

        /// <summary>
        /// The four analog channels a 24-byte v1 record had no room for, and that this game's movement
        /// genuinely needs (all short at FP scale, same convention as `look`, so 24+8 = 32 = Size):
        ///   auxA = inputDataX  - the strafe accumulator the controller carries between FixedUpdates
        ///   auxB = xvel         - what inputDataX has accumulated INTO; not derivable from the input
        ///   auxC = input_x      - the RESOLVED strafe the controller used, after tap/dynamic/uinput
        ///   auxD = yy           - ditto for forward, and forced to 1 by Autowalk/Tap control types
        /// C and D exist because GetInput() consults ButonManager.GetTapX() and SplitTouchControl.dt3,
        /// neither of which is in InputData: recording only `input` would have left the physics fed by
        /// an input the tape never saw. On replay the bridge writes them back into the controller
        /// (TasUnityBridge.TryResolvedInput) so the run is reproduced instead of re-derived from a
        /// touch queue that no longer holds anything.
        /// </summary>
        public short auxA, auxB, auxC, auxD;

        public float MoveXf { get { return moveX / (float)FP; } }
        public float MoveYf { get { return moveY / (float)FP; } }
        public float LookXf { get { return lookX / (float)FP; } }
        public float LookYf { get { return lookY / (float)FP; } }
        public Vector2 Move { get { return new Vector2(MoveXf, MoveYf); } }
        public Vector2 Look { get { return new Vector2(LookXf, LookYf); } }
        public Vector2 AimNorm
        {
            get
            {
                if (aimX == PointerAbsent) return new Vector2(-1f, -1f);
                return new Vector2(aimX / 65535f, aimY / 65535f);
            }
        }

        public bool Has(TasButton b) { return (buttons & (uint)b) != 0u; }
        public void Set(TasButton b, bool on) { buttons = on ? (buttons | (uint)b) : (buttons & ~(uint)b); }

        /// <summary>
        /// THE fold. Every hash in the tool - live recorder hash, splice re-fold, playback hash,
        /// tape-level FramesHash - must be this exact function in this exact order, or "desync 0"
        /// means nothing. It used to be four hand-written copies with three different field sets, and
        /// the recorder/checkpoint/playback comparisons could not match by construction.
        ///
        /// Input fields ONLY (including the resolved aux channels): positions are not in the tape, so
        /// a hash that folded them could not be recomputed after a rollback or by a verifier holding
        /// nothing but the file. State comparison lives in StateHash, one layer up.
        /// </summary>
        public uint Fold(uint h)
        {
            h = TasRng.Mix(h, (int)buttons);
            h = TasRng.Mix(h, (int)((moveX << 16) | (moveY & 0xFFFF)));
            h = TasRng.Mix(h, lookX);
            h = TasRng.Mix(h, lookY);
            h = TasRng.Mix(h, aimX | (aimY << 16));
            h = TasRng.Mix(h, (int)weaponSlot | ((int)flags << 8));
            h = TasRng.Mix(h, auxA);
            h = TasRng.Mix(h, auxB);
            h = TasRng.Mix(h, auxC);
            h = TasRng.Mix(h, auxD);
            return h;
        }

        /// <summary>
        /// Input hash + the one piece of sim state the tool can always read on both sides. Recorded
        /// into a checkpoint at capture time and rebuilt by playback at the same point of the same
        /// tick, so a mismatch means the SIM diverged, not that two different formulas were used.
        /// </summary>
        public static uint StateHash(uint inputHash, Vector3 pos)
        {
            inputHash = TasRng.Mix(inputHash, pos.x);
            inputHash = TasRng.Mix(inputHash, pos.y);
            inputHash = TasRng.Mix(inputHash, pos.z);
            return inputHash;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(buttons);
            w.Write(moveX);
            w.Write(moveY);
            w.Write(lookX);
            w.Write(lookY);
            w.Write(aimX);
            w.Write(aimY);
            w.Write(pointerCount);
            w.Write(weaponSlot);
            w.Write(flags);
            w.Write(pad);
            w.Write(auxA);
            w.Write(auxB);
            w.Write(auxC);
            w.Write(auxD);
        }

        public static TasInputFrame Read(BinaryReader r)
        {
            TasInputFrame f = new TasInputFrame();
            f.buttons = r.ReadUInt32();
            f.moveX = r.ReadInt16();
            f.moveY = r.ReadInt16();
            f.lookX = r.ReadInt32();
            f.lookY = r.ReadInt32();
            f.aimX = r.ReadUInt16();
            f.aimY = r.ReadUInt16();
            f.pointerCount = r.ReadByte();
            f.weaponSlot = r.ReadByte();
            f.flags = r.ReadByte();
            f.pad = r.ReadByte();
            f.auxA = r.ReadInt16();
            f.auxB = r.ReadInt16();
            f.auxC = r.ReadInt16();
            f.auxD = r.ReadInt16();
            return f;
        }

        /// <summary>Format v1 tapes: 24-byte records, no aux channels. Read-only support.</summary>
        public static TasInputFrame ReadLegacyV1(BinaryReader r)
        {
            TasInputFrame f = new TasInputFrame();
            f.buttons = r.ReadUInt32();
            f.moveX = r.ReadInt16();
            f.moveY = r.ReadInt16();
            f.lookX = r.ReadInt32();
            f.lookY = r.ReadInt32();
            f.aimX = r.ReadUInt16();
            f.aimY = r.ReadUInt16();
            f.pointerCount = r.ReadByte();
            f.weaponSlot = r.ReadByte();
            f.flags = r.ReadByte();
            f.pad = r.ReadByte();
            return f;
        }

        public float InputDataX { get { return auxA / (float)FP; } }
        public float Xvel { get { return auxB / (float)FP; } }
        public float ResolvedX { get { return auxC / (float)FP; } }
        public float ResolvedY { get { return auxD / (float)FP; } }

        public float AuxA { get { return auxA / (float)FP; } }
        public float AuxB { get { return auxB / (float)FP; } }
        public float AuxC { get { return auxC / (float)FP; } }
        public float AuxD { get { return auxD / (float)FP; } }

        /// <summary>Record the resolved GetInput() pair (integrity check on keyboard, input on Tap/Analog).</summary>
        public void ResolvedXSet(float x, float y)
        {
            auxC = (short)Mathf.Clamp(Mathf.RoundToInt(x * FP), -32767, 32767);
            auxD = (short)Mathf.Clamp(Mathf.RoundToInt(y * FP), -32767, 32767);
        }

        /// <summary>Record inputDataX and the xvel accumulator for the integrity check.</summary>
        public void ResolvedStrafe(float inputDataX, float xvel)
        {
            auxA = (short)Mathf.Clamp(Mathf.RoundToInt(inputDataX * FP), -32767, 32767);
            auxB = (short)Mathf.Clamp(Mathf.RoundToInt(xvel * FP), -32767, 32767);
        }

        public static int FromFloat(float v)
        {
            return Mathf.Clamp(Mathf.RoundToInt(v * FP), -FP, FP);
        }

        public static ushort FromNorm(float v)
        {
            if (v < 0f || v > 1f) return PointerAbsent;
            return (ushort)Mathf.Clamp(Mathf.RoundToInt(v * 65535f), 0, 65535);
        }

        public static int FromDeg(float deg)
        {
            // clamp to ~32k degrees/tick: a flick can't legitimately exceed that, and
            // clamping keeps a NaN from poisoning the tape.
            if (float.IsNaN(deg) || float.IsInfinity(deg)) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(deg * FP), int.MinValue / 2, int.MaxValue / 2);
        }

        public string Pretty()
        {
            return string.Format("m({0:0.00},{1:0.00}) look({2:0.0},{3:0.0}) aim({4:0.000},{5:0.000}) {6}",
                MoveXf, MoveYf, LookXf, LookYf,
                AimNorm.x, AimNorm.y,
                buttons == 0u ? "-" : System.Enum.Parse(typeof(TasButton), buttons.ToString()).ToString());
        }
    }

    [System.Flags]
    public enum TasFrameFlags : byte
    {
        None = 0,
        Teleport = 1 << 0,     // authored/edited frame, not captured from live play
        Resynced = 1 << 1,     // playback corrected toward a checkpoint here
        Lagged = 1 << 2,       // capture was starved this tick (should never happen with a tick clock)
        CursorUnlocked = 1 << 3,  // RotateView() returns early while unlocked, but lookAccum keeps
                                  // accruing the gain: recorded run and replay can only agree if the
                                  // cursor state is pinned, and this says when it was not.
        NaNState = 1 << 4,        // position/rotation went non-finite: the run is void, not slow
    }
}

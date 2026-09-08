using System;
using Tas;
using UnityStandardAssets.Characters.FirstPerson;
using UnityEngine;

/// <summary>
/// Drop this file into Assembly-CSharp (e.g. Assets/Scripts/) - NOT inside the Tas/ folder,
/// because it is the only place allowed to know about GameManager/GameState/InputManager.
/// The Tas assembly deliberately has no reference back into your game, so everything game-specific
/// is wired from here.
///
/// Why this exists instead of editing GameManager: GameManager already emits the three signals the
/// tool needs - `GameState.gameStarted` rising edge (StartGame), `GameManager.OnOyuncuDustu`
/// (run died) and `GameManager.OnLevelCompleted` (run finished, fired 0.1 s after the fall when
/// b_levelTamamlandi). So the bridge subscribes and polls, and your files stay untouched.
/// </summary>
[DefaultExecutionOrder(-33500)]
public sealed class TasGameBridge : MonoBehaviour
{
    public static TasGameBridge i;

    GameManager gm;
    bool wasGameStarted;
    float lastPoll;

    [Header("Ghost")]
    [Tooltip("Reuses the object your leaderboard demos already play on, so a TAS replay looks like a demo.")]
    public bool driveDemoModelAsGhost = true;

    [Header("Safety")]
    [Tooltip("Blocks the economy/quest writes that a rollback could otherwise farm. Read the comment.")]
    public bool suppressPersistenceWhileRecording = true;

    /// <summary>
    /// READ THIS BEFORE YOU SHIP THE TOOL. A savestate + an economy is a printer.
    ///
    /// Olay_OyuncuDustu writes, in one call: Prefs.GamesWon, QuestDailyGameCount / Score /
    /// PlayTime / LevelComplete, BoostCaseTimer (and hands out BoostCaseCount), LuckyWheelTimer,
    /// GameManagerHelpers.ScoreRegister(score, seconds), InventoryHelpers.Coin += bp, and
    /// CachedPrefs PInt 14/15/16. Every one of those is triggered by state the player controls, and
    /// with a TAS tool the player can now re-run the same 400 ticks forty times. Without this gate,
    /// "load slot 3, hit the score box, save slot 3" is an unlimited coin/quest loop, and the
    /// leaderboard time is derived from a tape you rewound.
    ///
    /// The rule the bridge enforces: while a TAS run is live, nothing persists. The writes are
    /// buffered by TasSuspend and flushed exactly once, at run end, for the FINAL trunk only.
    /// </summary>
    public static bool PersistenceAllowed
    {
        get
        {
            if (i == null || !i.suppressPersistenceWhileRecording) return true;
            return TasSuspend.Instance == null || !TasSuspend.Instance.Suspended;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (i != null) { i.TryBind(); return; }
        GameObject go = new GameObject("TasGameBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<TasGameBridge>();
    }

    void Awake()
    {
        i = this;
        TryBind();
        GameManager.OnLevelCompleted += OnLevelCompleted;
        GameManager.OnOyuncuDustu += OnPlayerFell;
    }

    void OnDestroy()
    {
        GameManager.OnLevelCompleted -= OnLevelCompleted;
        GameManager.OnOyuncuDustu -= OnPlayerFell;
        if (i == this) i = null;
    }

    /// <summary>
    /// GameManager.Start() only calls Baglantilar()/GravityAyarla() on some paths
    /// (waitreset_onsceneload / raceMode), so m_playerController and the gravity may not exist yet
    /// when we boot. Re-bind until they do, and hash the config AFTER gravity is set - in Surf it is
    /// -9.81*0.25 and everywhere else -13.5, so a tape recorded before GravityAyarla() and replayed
    /// after it would be silently wrong.
    /// </summary>
    public RigidbodyFirstPersonController Controller { get { return gm != null ? gm.m_playerController : null; } }
    public GameManager Manager { get { return gm; } }

    void TryBind()
    {
        if (gm == null) gm = GameState.gameManagerSingle != null ? GameState.gameManagerSingle : FindObjectOfType<GameManager>();
        if (gm == null || gm.m_playerController == null) return;

        TasRecorder.RaceModeProbe = delegate { return GameState.raceMode; };

        // Their own speed metric, their own formula, so the HUD/leaderboard number matches play.cs.
        RigidbodyFirstPersonController c = gm.m_playerController;
        if (TasRecorder.i != null)
            TasRecorder.i.SpeedReader = delegate
            {
                if (c == null) return 0;
                Vector3 v = c.velXZ;
                return Mathf.RoundToInt(Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * 51f);
            };

        TasRecorder.BindTimeManagerSeconds(delegate { return gm.m_TimeManager != null ? gm.m_TimeManager.Seconds : 0; });

        // Everything that changes what a tick MEANS. The controller's own tuning is in here too
        // (BunnyArtisHizi, BunnyMaxHiz, SurfMaxHiz, groundCheckDistance, analog_bunny_mult, ...)
        // because those fields are serialized per-prefab and someone WILL tweak them in the editor;
        // Prefs.Sensivity is in here because `if (sens < 1f) fromStrafe *= sens` makes sensitivity part
        // of the physics, and `if (!Application.isMobilePlatform) sensMultiplier = 1f` makes the
        // platform part of it. A tape is only meaningful relative to all of that.
        TasRecorder.ExtraConfigProbe = delegate
        {
            uint h = TasUnityBridge.ConfigExtras();
            h = TasRng.Mix(h, (int)Prefs.ControlType);
            h = TasRng.Mix(h, GameState.TapMode ? 1 : 0);
            h = TasRng.Mix(h, GameState.SurfSliding ? 2 : 0);
            h = TasRng.Mix(h, Physics.gravity.y);
            h = TasRng.Mix(h, Physics.gravity.z);
            h = TasRng.Mix(h, GameState.JumpForceCarpan);
            h = TasRng.Mix(h, GameState.ActiveBoost);
            if (GameState.i != null && GameState.i.ActiveMapScene != null)
            {
                h = TasRng.Mix(h, GameState.i.ActiveMapScene.oyunMode.ToString().GetHashCode());
                h = TasRng.Mix(h, GameState.i.ActiveMapScene.sceneName.GetHashCode());
            }
            return h;
        };

        if (TasTool.i != null)
        {
            TasTool.i.Clock.enforceFixedRate = TasTool.i.cfg.enforceFixedRate;
            if (driveDemoModelAsGhost && gm.playerDemoModel != null)
            {
                TasPlayback p = TasTool.i.Playback;
                p.rig = gm.playerDemoModel.transform;
                if (gm.cameraMain != null) p.rigCam = gm.cameraMain.transform;
            }
            TasTool.RegisterTickDriver(gm.m_playerController, "Update");

            if (suppressPersistenceWhileRecording && TasSuspend.Instance != null)
                TasTool.i.OnRecordStart += delegate { TasSuspend.Instance.Suspended = true; };
        }

        // the player's per-run accumulators are gameplay state: they feed the carry multiplier,
        // which feeds jump force, which feeds physics. So they MUST rewind with the run.
        TasPlayerRunStats stats = gm.m_player != null ? gm.m_player.GetComponent<TasPlayerRunStats>() : null;
        if (stats == null && gm.m_player != null) stats = gm.m_player.AddComponent<TasPlayerRunStats>();
        if (stats != null) stats.Bind(gm);

        TasUnityBridge.Bind(gm);
    }

    void Update()
    {
        if (gm == null) { TryBind(); return; }
        if (Time.unscaledTime - lastPoll < 0.05f) return;
        lastPoll = Time.unscaledTime;

        bool started = GameState.gameStarted;
        if (started && !wasGameStarted)
        {
            wasGameStarted = true;
            if (TasTool.i != null) TasTool.i.MarkGameStarted();
        }
        else if (!started && wasGameStarted)
        {
            wasGameStarted = false;
            if (TasTool.i != null && TasTool.i.session == TasSession.Recording)
                TasTool.NotifyMapFinished("gameStarted cleared");
        }
    }

    void OnLevelCompleted()
    {
        // NOTE: this fires 0.1 s AFTER the fall, via Invoke("LevelCompletedEvent", 0.1f), and the
        // player is already kinematic by then. So `frameCount` here is a few ticks past the line -
        // mark it and let the trim snap the tape to it.
        if (TasRecorder.i != null) TasRecorder.i.MarkFinish("level", gm != null && gm.m_TimeManager != null ? gm.m_TimeManager.Seconds : 0);
        TasTool.NotifyMapFinished("level");
        if (TasSuspend.Instance != null) TasSuspend.Instance.Flush();
    }

    void OnPlayerFell()
    {
        if (TasRecorder.i != null) TasRecorder.i.MarkFinish(GameState.b_levelTamamlandi ? "level+fall" : "death",
                                                            gm != null && gm.m_TimeManager != null ? gm.m_TimeManager.Seconds : 0);
        TasTool.NotifyMapFinished("death");
        if (TasSuspend.Instance != null) TasSuspend.Instance.Flush();
    }

    public void Rebind() { TryBind(); }
}

/// <summary>
/// Per-run state on the player that a transform+velocity snapshot cannot reconstruct.
///
/// The list is not arbitrary: it is every field this controller writes but never resets, which is
/// the same thing StartGame() treats as run-scoped. Three of them are load-bearing for physics:
///   lookAccum      - Update() accumulates it, FixedUpdate() spends AND CLEARS it. Capturing in
///                    LateUpdate is what makes a restore land on the right side of that handoff;
///                    capturing at the tick tail would restore 0 and drop a frame of turn.
///   lastBunnyFrame - gates the one-push-per-input-frame (`if (lastBunnyFrame != inputData.inputLastFrame)`)
///   active180      - selects the Don180MaxHiz clamp, and a coroutine clears it 0.2 s later
/// plus xvel (a clamped accumulator), m_bunnyCarpan/_bmax/mag/ag (read across the Update/FixedUpdate
/// boundary while still holding the previous step's values), the total_*/max_* carry stats, and
/// LevelFizik.MaxHizYDisabled / GameState.TapMode / SurfModeFizik / ControlTypeCache, which are
/// STATIC-ish and therefore invisible to any per-component snapshot - the sneakiest class of desync.
/// </summary>
public sealed class TasPlayerRunStats : MonoBehaviour, ITasSnapshotable
{
    GameManager gm;
    UnityStandardAssets.Characters.FirstPerson.RigidbodyFirstPersonController c;

    public void Bind(GameManager manager)
    {
        gm = manager;
        c = manager != null ? manager.m_playerController : null;
        TasSnapshot.Register((ITasSnapshotable)this);
    }

    void OnDisable() { TasSnapshot.Unregister((ITasSnapshotable)this); }

    public void TasSave(System.IO.BinaryWriter w)
    {
        UnityTAS.TasUnityBridge.SaveController(c, w);

        w.Write(GameState.b_levelTamamlandi);
        w.Write(GameState.DieOnLevelComplete);
        w.Write(GameState.b_casualBaslangicAyarlandi);
        w.Write(GameState.JumpForceCarpan);
        w.Write(GameState.LastPlane);
        w.Write(ScoreCollision.totalScoreCollision);
        w.Write(LevelSonu.KaydedilenPuan);
        if (gm != null && gm.m_ScoreManager != null) w.Write(gm.m_ScoreManager.Score); else w.Write(0);
        w.Write(UnityTAS.TasSuspendState.Suspended);
    }

    public void TasLoad(System.IO.BinaryReader r)
    {
        UnityTAS.TasUnityBridge.LoadController(c, r);

        GameState.b_levelTamamlandi = r.ReadBoolean();
        GameState.DieOnLevelComplete = r.ReadBoolean();
        GameState.b_casualBaslangicAyarlandi = r.ReadBoolean();
        GameState.JumpForceCarpan = r.ReadSingle();
        GameState.LastPlane = r.ReadVector3();
        ScoreCollision.totalScoreCollision = r.ReadInt32();
        LevelSonu.KaydedilenPuan = r.ReadInt32();
        int score = r.ReadInt32();
        bool suspended = r.ReadBoolean();
        if (gm != null && gm.m_ScoreManager != null) gm.m_ScoreManager.Score = score;
        UnityTAS.TasSuspendState.Suspended = suspended;

        // ⚠ TimeManager stays out of the snapshot on purpose: it is a wall/HUD clock and the tape's
        // tick count is the run time. Add a Restore(int) to TimeManager if you want the HUD to rewind
        // too; do not make gameplay read it while the tool is live.
    }
}

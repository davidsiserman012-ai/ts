using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A one-flag "do not persist" gate for TAS sessions, plus a tiny deferred-write buffer.
///
/// Kept separate so it is obvious in review: every Prefs / CachedPrefs / InventoryHelpers /
/// ScoreRegister / quest write you care about should consult PersistenceAllowed before touching
/// disk. That is a handful of one-line guards in Prefs setters (best) or at the call sites in
/// GameManager.Olay_OyuncuDustu (fastest). A TAS tool without this gate is a farm.
/// </summary>
public sealed class TasSuspend : MonoBehaviour
{
    public static TasSuspend Instance { get; private set; }

    public bool Suspended;
    public int Buffered;
    public int Flushed;

    readonly List<Action> deferred = new List<Action>(32);

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>Call from a Prefs setter: `if (!TasGameBridge.PersistenceAllowed) { Enqueue(() => real = v); return; }`</summary>
    public void Enqueue(Action write)
    {
        if (!Suspended) { if (write != null) write(); return; }
        if (write != null) { deferred.Add(write); Buffered++; }
    }

    /// <summary>Run end: commit everything the attempts accumulated, once, for the final trunk.</summary>
    public void Flush()
    {
        Suspended = false;
        int n = deferred.Count;
        for (int k = 0; k < n; k++)
        {
            try { deferred[k](); }
            catch (Exception e) { Debug.LogWarning("[TAS] deferred write: " + e.Message); }
        }
        deferred.Clear();
        Flushed += n;
        Buffered = 0;
    }

    /// <summary>Discard the attempts that were rewound away instead of committing them.</summary>
    public void Abandon() { deferred.Clear(); Buffered = 0; }
}

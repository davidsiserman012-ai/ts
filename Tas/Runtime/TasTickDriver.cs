using System;
using System.Reflection;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Makes slow-mo, turbo and frame-advance work with sim scripts you will not restructure.
    ///
    /// The problem: manual mode has to stop the world, and the only global "stop" Unity offers
    /// is Time.timeScale = 0 - which also stops YOUR controller from running, so a frozen world
    /// plus one Physics.Simulate call means the player never moves during playback. Sim scripts
    /// must therefore be driven by the tick bus, not by the engine. Two ways to get that:
    ///
    ///   A. Patch the script (recommended, permanent, ~2 lines):
    ///        void Update() { if (TasClock.Manual) return; Step(); }
    ///        public  void  Step()  { ...the original body... }
    ///      and make the body read TasClock.Delta instead of Time.deltaTime.
    ///   B. Attach this component (no source edits): it disables the target so the engine stops
    ///      calling it, then invokes its Update/FixedUpdate exactly once per TAS tick.
    ///
    /// B uses reflection on a private method. That is fine in the Editor and Mono, and fine in
    /// IL2CPP *if* you keep the method from being stripped: put the controller's type in
    /// link.xml ([preserve type="..."] ) or add [Preserve]. If a release build ever shows
    /// "playback does nothing but physics moves", this is the first thing to check.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public sealed class TasTickDriver : MonoBehaviour
    {
        [Tooltip("The sim behaviour to drive, e.g. the RigidbodyFirstPersonController on the player.")]
        public MonoBehaviour target;
        public string methodName = "Update";

        MethodInfo mi;
        bool engineDriven = true;
        int invokeErrors;

        void Awake()
        {
            if (target == null) target = GetComponent<MonoBehaviour>();
            Resolve();
            if (TasClock.Exists) TasClock.i.OnSimTick += OnTick;
        }

        void OnDestroy()
        {
            if (TasClock.Exists) TasClock.i.OnSimTick -= OnTick;
            if (target != null) target.enabled = true;
        }

        void Resolve()
        {
            mi = null;
            if (target == null) return;
            Type t = target.GetType();
            while (t != null && mi == null)
            {
                mi = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.DeclaredOnly,
                                 null, Type.EmptyTypes, null);
                t = t.BaseType;
            }
            if (mi == null)
                Debug.LogWarning("[TAS] TasTickDriver: no parameterless '" + methodName + "' on " +
                                 target.GetType().Name + " - attach it to a script that has one, or use option A");
        }

        void Update()
        {
            // flip who owns the step, once, at the mode boundary - never mid-tick
            bool wantEngine = !TasClock.Manual;
            if (wantEngine == engineDriven) return;
            engineDriven = wantEngine;
            if (target != null) target.enabled = wantEngine;
            if (!wantEngine) TasLiveInput.ResyncCursor();
        }

        void OnTick(float dt)
        {
            if (mi == null || target == null) return;
            if (engineDriven) return;               // the engine is already calling it
            try { mi.Invoke(target, null); }
            catch (Exception e)
            {
                if (invokeErrors++ < 3)
                    Debug.LogError("[TAS] tick driver invoke failed on " + target.GetType().Name + "." +
                                   methodName + ": " + e.Message);
            }
        }

        public static TasTickDriver Attach(MonoBehaviour targetBehaviour, string method = "Update")
        {
            if (targetBehaviour == null) return null;
            TasTickDriver[] existing = targetBehaviour.GetComponents<TasTickDriver>();
            for (int k = 0; k < existing.Length; k++)
                if (existing[k].target == targetBehaviour) return existing[k];

            GameObject go = new GameObject("TasTickDriver[" + targetBehaviour.GetType().Name + "." + method + "]");
            go.transform.SetParent(targetBehaviour.transform, false);
            TasTickDriver d = go.AddComponent<TasTickDriver>();   // Awake wires target + bus
            d.target = targetBehaviour;
            d.methodName = method;
            d.Resolve();
            TasTool.RegisterTickDriver(d);
            return d;
        }
    }
}

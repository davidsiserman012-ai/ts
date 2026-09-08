using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Thin shim over the physics stepping API, because the property names moved in 2022.2.
    /// </summary>
    public static class TasPhysics
    {
        public static void SetManual(bool on)
        {
#if UNITY_2022_2_OR_NEWER
            Physics.simulationMode = on ? SimulationMode.Manual : SimulationMode.FixedUpdate;
#else
            Physics.autoSimulation = !on;
#endif
        }

        /// <summary>
        /// Deterministic single step. SyncTransforms before the step is not optional:
        /// whether the physics scene sees this frame's hierarchy moves is an ordering
        /// dependency, and an ordering dependency is a desync.
        /// </summary>
        public static void Step(float dt)
        {
            Physics.SyncTransforms();
            Physics.Simulate(dt);
        }

        public static void Capture(ref Rigidbody rb, ref Transform t, ref TasRigidbodyState s)
        {
            s.pos = t.position;
            s.rot = t.rotation;
            s.vel = rb != null ? rb.velocity : Vector3.zero;
            s.angVel = rb != null ? rb.angularVelocity : Vector3.zero;
            s.kinematic = rb != null && rb.isKinematic;
            // isSleeping is not cosmetic here: this controller calls m_RigidBody.Sleep() when it is
            // nearly still, and a sleeping body ignores AddRelativeForce until something wakes it.
            // Roll back to a sleeping frame and replay wakes it a tick earlier than the run did.
            s.sleeping = rb != null && rb.IsSleeping();
            s.gravity = rb == null || rb.useGravity;
        }

        public static void Restore(ref Rigidbody rb, ref Transform t, ref TasRigidbodyState s)
        {
            t.position = s.pos;
            t.rotation = s.rot;
            if (rb != null)
            {
                rb.isKinematic = s.kinematic;
                rb.velocity = s.vel;
                rb.angularVelocity = s.angVel;
                rb.useGravity = s.gravity;
                if (s.sleeping) rb.Sleep(); else rb.WakeUp();
            }
        }
    }

    public struct TasRigidbodyState
    {
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public Vector3 angVel;
        public bool kinematic;
        public bool sleeping;
        public bool gravity;
    }
}

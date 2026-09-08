namespace Tas
{
    /// <summary>
    /// Single compile-time switch for whether the tool exists in a build at all.
    ///
    /// A runtime bool (TasToolConfig.toolEnabled) is the wrong answer for a game with a leaderboard:
    /// the code is still in the player, so it can be flipped by editing config.json, and the savestate
    /// / tape types are still reachable. With a define, a release build never creates TasTool, never
    /// registers hotkeys, and never compiles the input-injection path into the shipped assembly.
    ///
    /// Enable it in a build with Scripting Define Symbols = TAS_TOOL (or just use a Development Build,
    /// which is what you want for sending tapes to yourself anyway).
    /// </summary>
    public static class TasGate
    {
#if TAS_TOOL || UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool Available = true;
#else
        public const bool Available = false;
#endif

        /// <summary>Why it is off, for a single line in the console rather than silence.</summary>
        public static string ReasonIfUnavailable
        {
            get
            {
                return Available ? null :
                    "TAS tool compiled out: add TAS_TOOL to Scripting Define Symbols, or use a Development Build";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BetterRawAccel.Core
{
    /// <summary>
    /// Decides which profile per-game rules want. It reports a profile only when the wanted profile changes
    /// (a game started or stopped), so switching by hand while a game runs is not undone two seconds later.
    /// </summary>
    public sealed class GameWatch
    {
        string last;
        bool primed;

        public static string CleanProcessName(string name)
        {
            var n = (name ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        /// <summary>The first rule whose program is running, or null.</summary>
        public static GameRule Running(IEnumerable<GameRule> rules, Func<string, bool> isRunning)
        {
            foreach (var r in rules)
                if (isRunning(CleanProcessName(r.Process))) return r;
            return null;
        }

        public static bool IsRunning(string processName)
        {
            var ps = Process.GetProcessesByName(processName);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        /// <summary>Returns a profile to switch to, or null when nothing should change.</summary>
        public string Next(IList<GameRule> rules, string fallback, Func<string, bool> isRunning)
        {
            if (rules == null || rules.Count == 0) { last = null; primed = false; return null; }
            var hit = Running(rules, isRunning);
            string want = hit != null ? hit.Profile : (string.IsNullOrEmpty(fallback) ? null : fallback);
            bool changed = !primed || want != last;
            primed = true;
            last = want;
            return changed ? want : null;
        }

        /// <summary>Forget the last decision, so the next check applies the rules again.</summary>
        public void Reset() { primed = false; last = null; }
    }
}

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BetterRawAccel.Core;

namespace BetterRawAccel.Tests
{
    /// <summary>Per-game profile rules: when they switch, and when they must leave a manual choice alone.</summary>
    [TestClass]
    public class GameWatchTests
    {
        static readonly List<GameRule> Rules = new List<GameRule>
        {
            new GameRule { Process = "VALORANT-Win64-Shipping.exe", Profile = "Valorant" },
            new GameRule { Process = "cs2", Profile = "CS" },
        };

        [TestMethod]
        public void ProcessNamesLoseExeAndSpaces()
        {
            Assert.AreEqual("VALORANT-Win64-Shipping", GameWatch.CleanProcessName(" VALORANT-Win64-Shipping.EXE "));
            Assert.AreEqual("cs2", GameWatch.CleanProcessName("cs2"));
            Assert.AreEqual("", GameWatch.CleanProcessName(null));
        }

        [TestMethod]
        public void FirstCheckAppliesTheFallback()
        {
            var w = new GameWatch();
            Assert.AreEqual("Desktop", w.Next(Rules, "Desktop", p => false));
        }

        [TestMethod]
        public void SwitchesOnlyWhenAGameStartsOrStops()
        {
            var w = new GameWatch();
            bool valo = false;
            Func<string, bool> running = p => valo && p == "VALORANT-Win64-Shipping";

            Assert.AreEqual("Desktop", w.Next(Rules, "Desktop", running));
            Assert.IsNull(w.Next(Rules, "Desktop", running), "nothing changed, so a manual switch must be left alone");

            valo = true;
            Assert.AreEqual("Valorant", w.Next(Rules, "Desktop", running));
            Assert.IsNull(w.Next(Rules, "Desktop", running), "still in game: no repeated switching");

            valo = false;
            Assert.AreEqual("Desktop", w.Next(Rules, "Desktop", running));
        }

        [TestMethod]
        public void EmptyFallbackLeavesTheProfileAlone()
        {
            var w = new GameWatch();
            Assert.IsNull(w.Next(Rules, "", p => false));
            Assert.AreEqual("CS", w.Next(Rules, "", p => p == "cs2"));
            Assert.IsNull(w.Next(Rules, "", p => false), "game closed with no fallback: stay on the game's profile");
        }

        [TestMethod]
        public void FirstListedRuleWins()
        {
            var w = new GameWatch();
            Assert.AreEqual("Valorant", w.Next(Rules, "", p => true));
        }

        [TestMethod]
        public void NoRulesNeverSwitches()
        {
            var w = new GameWatch();
            Assert.IsNull(w.Next(new List<GameRule>(), "Desktop", p => true));
        }

        [TestMethod]
        public void ResetAppliesTheRulesAgain()
        {
            var w = new GameWatch();
            Assert.AreEqual("Desktop", w.Next(Rules, "Desktop", p => false));
            w.Reset();
            Assert.AreEqual("Desktop", w.Next(Rules, "Desktop", p => false));
        }

        [TestMethod]
        public void KnownGamesAreRecognisedAndUnknownOnesAreNot()
        {
            Assert.IsTrue(SensitivityCalculator.IsKnownGame("valorant"));
            Assert.IsTrue(SensitivityCalculator.IsKnownGame(" CS2 "));
            Assert.IsFalse(SensitivityCalculator.IsKnownGame("chess"));
            Assert.IsFalse(SensitivityCalculator.IsKnownGame(""));
            Assert.AreEqual("Valorant", SensitivityCalculator.DisplayName("valorant"));
        }
    }
}

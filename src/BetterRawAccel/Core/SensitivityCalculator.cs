using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    public static class SensitivityCalculator
    {
        // Standard game yaw values (degrees rotation per count at sensitivity 1.0)
        static readonly Dictionary<string, double> StandardYaws = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = 0.022,
            ["cs2"] = 0.022,
            ["csgo"] = 0.022,
            ["apex"] = 0.022,
            ["quake"] = 0.022,
            ["titanfall"] = 0.022,
            ["teamfortress2"] = 0.022,
            ["callofduty"] = 0.022,
            ["cod"] = 0.022,
            ["warzone"] = 0.022,
            ["valorant"] = 0.07,
            ["overwatch"] = 0.0066,
            ["overwatch2"] = 0.0066,
            ["fortnite"] = 0.005555,
            ["rainbow6"] = 0.00572,
            ["r6"] = 0.00572,
            ["destiny2"] = 0.0066,
        };

        public static double GetYaw(string game, double customYaw = 0)
        {
            if (customYaw > 0) return customYaw;
            if (string.IsNullOrWhiteSpace(game)) return 0.022; // default to Source / CS2
            return StandardYaws.TryGetValue(game.Trim(), out double yaw) ? yaw : 0.022;
        }

        public static double Cm360(double dpi, double gameSens, double yaw)
        {
            if (dpi <= 0 || gameSens <= 0 || yaw <= 0) return 0;
            double degPerCount = gameSens * yaw;
            double countsPer360 = 360.0 / degPerCount;
            double inchesPer360 = countsPer360 / dpi;
            return inchesPer360 * 2.54;
        }

        public static double In360(double dpi, double gameSens, double yaw)
        {
            double cm = Cm360(dpi, gameSens, yaw);
            return cm > 0 ? cm / 2.54 : 0;
        }

        public static double ConvertSens(string fromGame, double fromSens, double fromDpi, string toGame, double toDpi, double customFromYaw = 0, double customToYaw = 0)
        {
            double fromYaw = GetYaw(fromGame, customFromYaw);
            double toYaw = GetYaw(toGame, customToYaw);
            if (fromDpi <= 0 || toDpi <= 0 || toYaw <= 0 || fromSens <= 0) return 0;
            return (fromSens * fromYaw / toYaw) * (fromDpi / toDpi);
        }

        public static JObject Analyze(Engine engine, Profile p, double dpi, string game, double gameSens, double customYaw = 0, double? speed = null)
        {
            if (dpi <= 0) dpi = engine?.App?.PlotDpi > 0 ? engine.App.PlotDpi : 800;
            if (gameSens <= 0) gameSens = 1.0;
            double yaw = GetYaw(game, customYaw);

            double baseCm = Cm360(dpi, gameSens, yaw);
            double baseIn = In360(dpi, gameSens, yaw);

            double profSens = p != null && p.outputDPI > 0 ? p.outputDPI / 1000.0 : 1.0;

            // Sample curve to find min, max and current effective multiplier
            double evalSpeed = speed.HasValue && speed.Value >= 0 ? speed.Value : 0;
            double currentMult = 1.0;
            double minMult = double.MaxValue;
            double maxMult = double.MinValue;

            if (engine != null && p != null && Engine.Validate(p) == null)
            {
                try
                {
                    currentMult = Math.Max(0.0001, engine.Evaluate(p, evalSpeed > 0 ? evalSpeed : 0.001, false) * profSens);
                }
                catch
                {
                    currentMult = profSens;
                }

                // Sample 50 points across 0..maxSpeed
                double maxSpd = engine.MaxSpeed;
                for (int i = 0; i < 50; i++)
                {
                    double spd = Math.Max(0.001, maxSpd * i / 49.0);
                    try
                    {
                        double m = engine.Evaluate(p, spd, false) * profSens;
                        if (m < minMult) minMult = m;
                        if (m > maxMult) maxMult = m;
                    }
                    catch { }
                }
            }

            if (minMult == double.MaxValue) minMult = profSens;
            if (maxMult == double.MinValue) maxMult = profSens;

            double currentCm = currentMult > 0 ? baseCm / currentMult : baseCm;
            double fastestCm = maxMult > 0 ? baseCm / maxMult : baseCm;
            double slowestCm = minMult > 0 ? baseCm / minMult : baseCm;

            var equivalents = new JObject
            {
                ["source_cs2_apex"] = Math.Round(ConvertSens(game, gameSens, dpi, "source", dpi, customYaw, 0), 4),
                ["valorant"] = Math.Round(ConvertSens(game, gameSens, dpi, "valorant", dpi, customYaw, 0), 4),
                ["overwatch2"] = Math.Round(ConvertSens(game, gameSens, dpi, "overwatch2", dpi, customYaw, 0), 4),
                ["fortnite"] = Math.Round(ConvertSens(game, gameSens, dpi, "fortnite", dpi, customYaw, 0), 4),
                ["rainbow6"] = Math.Round(ConvertSens(game, gameSens, dpi, "rainbow6", dpi, customYaw, 0), 4),
            };

            return new JObject
            {
                ["game"] = game ?? "source",
                ["gameSens"] = Math.Round(gameSens, 4),
                ["dpi"] = (int)dpi,
                ["yaw"] = Math.Round(yaw, 6),
                ["baseCm360"] = Math.Round(baseCm, 2),
                ["baseIn360"] = Math.Round(baseIn, 2),
                ["evalSpeed"] = Math.Round(evalSpeed, 3),
                ["currentMultiplier"] = Math.Round(currentMult, 4),
                ["currentCm360"] = Math.Round(currentCm, 2),
                ["fastestFlickCm360"] = Math.Round(fastestCm, 2),
                ["slowestTrackingCm360"] = Math.Round(slowestCm, 2),
                ["equivalents"] = equivalents
            };
        }
    }
}

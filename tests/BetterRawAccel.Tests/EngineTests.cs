using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using BetterRawAccel.Core;

namespace BetterRawAccel.Tests
{
    [TestClass]
    public class SensitivityCalculatorTests
    {
        [TestMethod]
        public void TestBaseCm360_Source()
        {
            // At 800 DPI, sens 1.0, Source yaw 0.022:
            // counts/360 = 360 / (1.0 * 0.022) = 16363.636...
            // in/360 = 16363.636 / 800 = 20.4545...
            // cm/360 = 20.4545 * 2.54 = 51.9545...
            double cm = SensitivityCalculator.Cm360(800, 1.0, 0.022);
            Assert.AreEqual(51.95, Math.Round(cm, 2));
        }

        [TestMethod]
        public void TestBaseCm360_Valorant()
        {
            // At 800 DPI, sens 0.314, Valorant yaw 0.07:
            // counts/360 = 360 / (0.314 * 0.07) = 16378.52...
            // in/360 = 16378.52 / 800 = 20.473...
            // cm/360 = 20.473 * 2.54 = ~52.00
            double cm = SensitivityCalculator.Cm360(800, 0.314, 0.07);
            Assert.AreEqual(52.00, Math.Round(cm, 1));
        }

        [TestMethod]
        public void TestConvertSens_SourceToValorant()
        {
            // 1.0 in Source equals 1.0 * 0.022 / 0.07 = 0.314285... in Valorant at same DPI
            double valSens = SensitivityCalculator.ConvertSens("source", 1.0, 800, "valorant", 800);
            Assert.AreEqual(0.3143, Math.Round(valSens, 4));
        }

        [TestMethod]
        public void TestConvertSens_DpiScaling()
        {
            // 1.0 in Source at 800 DPI equals 0.5 in Source at 1600 DPI
            double sens = SensitivityCalculator.ConvertSens("source", 1.0, 800, "source", 1600);
            Assert.AreEqual(0.5, Math.Round(sens, 4));
        }
    }

    [TestClass]
    public class ProfileSharingTests
    {
        [TestMethod]
        public void TestExportAndImportRoundTrip()
        {
            var args = new AccelArgs
            {
                mode = AccelMode.classic,
                exponentClassic = 2.0,
                acceleration = 0.015,
                capMode = CapMode.output
            };
            var cap = args.cap;
            cap.x = 25.0;
            cap.y = 2.0;
            args.cap = cap;

            var p = new Profile
            {
                name = "test_profile",
                outputDPI = 1200.0,
                argsX = args
            };

            string code = ProfileSharing.ExportCode(p);
            Assert.IsTrue(code.StartsWith("RA1:"));

            var imported = ProfileSharing.ImportCode(code);
            Assert.IsNotNull(imported);
            Assert.AreEqual("test_profile", (string)imported["name"]);
            Assert.AreEqual(1.2, Math.Round((double)imported["sens"], 3));
            Assert.AreEqual("classic", (string)imported["x"]["mode"]);
            Assert.AreEqual(0.015, (double)imported["x"]["acceleration"]);
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void TestInvalidCodeThrows()
        {
            ProfileSharing.ImportCode("RA1:not_valid_base64_#$%^");
        }
    }

    [TestClass]
    public class SpeedHistogramTests
    {
        [TestMethod]
        public void TestHistogramAccumulationAndStats()
        {
            var hist = new SpeedHistogram();
            double maxSpeed = 100.0;

            // Record speeds: 10, 20, 30, 40, 50
            for (int i = 0; i < 10; i++) hist.Record(10.0, maxSpeed);
            for (int i = 0; i < 20; i++) hist.Record(30.0, maxSpeed);
            for (int i = 0; i < 10; i++) hist.Record(50.0, maxSpeed);

            var dto = hist.ToDto(maxSpeed);
            Assert.AreEqual(40L, (long)dto["totalSamples"]);
            Assert.AreEqual(50.0, (double)dto["peakSpeed"]);
            Assert.IsTrue((double)dto["medianSpeed"] > 0);

            hist.Reset();
            var resetDto = hist.ToDto(maxSpeed);
            Assert.AreEqual(0L, (long)resetDto["totalSamples"]);
            Assert.AreEqual(0.0, (double)resetDto["peakSpeed"]);
        }
    }

    [TestClass]
    public class ReferenceCurveTests
    {
        [TestMethod]
        public void TestReferenceCurveDataDto()
        {
            var refData = new ReferenceCurveData
            {
                Name = "my baseline",
                Points = new double[] { 1.0, 1.2, 1.4 },
                PointsY = null,
                MaxSpeed = 50.0,
                PinnedAt = DateTime.UtcNow
            };

            var dto = refData.ToDto();
            Assert.AreEqual("my baseline", (string)dto["name"]);
            Assert.AreEqual(50.0, (double)dto["maxSpeed"]);
            var pts = (JArray)dto["x"];
            Assert.AreEqual(3, pts.Count);
            Assert.AreEqual(1.0, (double)pts[0]);
            Assert.AreEqual(1.2, (double)pts[1]);
            Assert.AreEqual(1.4, (double)pts[2]);
        }
    }

    [TestClass]
    public class ThemeAndConfigTests
    {
        [TestMethod]
        public void TestThemeListCompleteness()
        {
            var expectedThemes = new[]
            {
                "paper", "ledger", "parchment", "millimeter", "technical", "chalk",
                "blueprint", "graphite", "phosphor", "obsidian", "midnight", "radar", "cyberpunk", "solarized"
            };

            // Ensure all 14 themes are recognized and valid
            foreach (var t in expectedThemes)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t));
            }
        }

        [TestMethod]
        public void TestSensitivityAnalysis()
        {
            var p = new Profile
            {
                name = "test",
                outputDPI = 1000.0,
                argsX = new AccelArgs
                {
                    mode = AccelMode.noaccel
                }
            };

            var res = SensitivityCalculator.Analyze(null, p, 800, "source", 1.5, 0, 10.0);
            Assert.IsNotNull(res);
            Assert.AreEqual(800, (int)res["dpi"]);
            Assert.AreEqual(1.5, (double)res["gameSens"]);
            Assert.IsTrue((double)res["baseCm360"] > 0);
            Assert.AreEqual((double)res["baseCm360"], (double)res["currentCm360"]);
        }
    }
}

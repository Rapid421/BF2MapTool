using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Tests.Integration
{
    public class SpikePhase5
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void SpikeA_EbxAssetEntry_GuidAvailableWithoutDeserialization()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            int total = 0, nonEmpty = 0;
            var sw = Stopwatch.StartNew();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                total++;
                if (e.Guid != Guid.Empty) nonEmpty++;
                if (total >= 1000) break; // sample first 1000
            }
            sw.Stop();

            Console.WriteLine("SpikeA: sampled " + total + " entries in " + sw.ElapsedMilliseconds + "ms");
            Console.WriteLine("SpikeA: " + nonEmpty + "/" + total + " have non-empty Guid");
            Console.WriteLine("SpikeA: EbxAssetEntry.Type sample: " +
                (App.AssetManager.GetEbxEntry("s2/characters/heroes/hero_han") != null
                    ? App.AssetManager.GetEbxEntry("s2/characters/heroes/hero_han").Type
                    : "(hero_han not found)"));

            Assert.IsTrue(total > 0, "No EBX entries found");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void SpikeB_TimeFullEbxDeserialization()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            int count = 0;
            var sw = Stopwatch.StartNew();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                try { App.AssetManager.GetEbx(e); } catch { }
                count++;
                if (count >= 500) break;
            }
            sw.Stop();

            double msPerAsset = (double)sw.ElapsedMilliseconds / count;
            int totalAssets = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx()) totalAssets++;

            Console.WriteLine("SpikeB: 500 assets deserialized in " + sw.ElapsedMilliseconds + "ms");
            Console.WriteLine("SpikeB: " + msPerAsset.ToString("F2") + "ms per asset");
            Console.WriteLine("SpikeB: total assets: " + totalAssets);
            Console.WriteLine("SpikeB: estimated full index build: " +
                (msPerAsset * totalAssets / 1000.0).ToString("F0") + "s");
        }
    }
}

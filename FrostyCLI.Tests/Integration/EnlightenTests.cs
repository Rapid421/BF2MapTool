using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class EnlightenTests
    {
        private static string FindCliExe()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string exe = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                "FrostyCLI", "bin", "x64", "Release", "FrostyCLI.exe"));
            return File.Exists(exe) ? exe : null;
        }

        private static int Run(string exe, string args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process process = Process.Start(psi))
            {
                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(180000)) { try { process.Kill(); } catch { } return -1; }
                return process.ExitCode;
            }
        }

        [Test]
        [IntegrationTest("swbf1")]
        public void Swbf1CarboniteChamberReportsStaticDatabaseAndTextures()
        {
            string game = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }
            string stdout, stderr;
            int rc = Run(exe, "--quiet --game swbf1 --game-path \"" + game + "\" enlighten inspect "
                + "\"xp2/levels/clouds/clouds_04/clouds_04\" --format json", out stdout, out stderr);
            Assert.IsTrue(rc == 0, "rc=" + rc + "; stderr=" + stderr);
            JObject json = JObject.Parse(stdout);
            Assert.IsTrue(json["entities"].ToString().Contains("StaticEnlightenEntityData"));
            Assert.IsTrue(json["assets"].ToString().Contains("3335336582"));
            Assert.IsTrue(json["assets"].ToString().Contains("irradianceTexture"));
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void Swbf2CloudCityReportsFluxAndRgbDirectionTextures()
        {
            string game = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }
            string stdout, stderr;
            int rc = Run(exe, "--quiet --game swbf2 --game-path \"" + game + "\" enlighten inspect "
                + "\"s2/levels/cloudcity_01/cloudcity_01\" --format json", out stdout, out stderr);
            Assert.IsTrue(rc == 0, "rc=" + rc + "; stderr=" + stderr);
            JObject json = JObject.Parse(stdout);
            Assert.IsTrue(json["entities"].ToString().Contains("fluxAutoBake"));
            Assert.IsTrue(json["assets"].ToString().Contains("3335336582"));
            Assert.IsTrue(json["assets"].ToString().Contains("StaticDirectionTextureB"));
        }
    }
}

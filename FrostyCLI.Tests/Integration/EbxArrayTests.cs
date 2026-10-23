using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // G1: `ebx array-add` / `ebx array-remove` -- list element add/remove (a GUI capability the CLI
    // previously lacked; `ebx set` could only overwrite an existing index).
    public class EbxArrayTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        // Finds an EBX whose root has a non-null List property with a string/CString element type
        // (so array-add can append a coerced --value). Avoids level assets (mutating those pollutes
        // later level tests in this shared process).
        private static EbxAssetEntry FindStringList(out string propName)
        {
            propName = null;
            int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (++scanned > 400) break;
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (PropertyInfo p in a.RootObject.GetType().GetProperties())
                {
                    if (!p.CanRead) continue;
                    Type pt = p.PropertyType;
                    if (pt == typeof(string)) continue;
                    if (!typeof(IList).IsAssignableFrom(pt)) continue;
                    if (!pt.IsGenericType) continue;
                    Type et = pt.GetGenericArguments()[0];
                    if (et != typeof(string) && et.Name != "CString") continue;
                    object val;
                    try { val = p.GetValue(a.RootObject); } catch { continue; }
                    if (val == null) continue;
                    propName = p.Name;
                    return e;
                }
            }
            return null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxArrayAddRemove_RoundTrip_CountChanges()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry target = FindStringList(out string prop);
            if (target == null || prop == null)
            { Console.WriteLine("skip: no asset with a non-null string/CString list property found"); return; }

            Func<int> count = () =>
            {
                var a = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(target.Name));
                return ((IList)a.RootObject.GetType().GetProperty(prop).GetValue(a.RootObject)).Count;
            };

            try
            {
                int before = count();
                string token = "FROSTYCLI_ARR_" + Guid.NewGuid().ToString("N").Substring(0, 6);

                int rAdd = EbxWriteCommand.ExecuteArrayAdd(
                    new[] { target.Name, "--prop", prop, "--value", token }, logger, fmt);
                Assert.AreEqual(0, rAdd);
                Assert.AreEqual(before + 1, count());
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified, "asset should be modified after array-add");

                int rRem = EbxWriteCommand.ExecuteArrayRemove(
                    new[] { target.Name, "--prop", prop, "--index", before.ToString() }, logger, fmt);
                Assert.AreEqual(0, rRem);
                Assert.AreEqual(before, count());
            }
            finally
            {
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
            }
        }

        // Guard: array-remove with an out-of-range index returns 1 (no crash).
        [Test]
        [IntegrationTest("swbf2")]
        public void EbxArrayRemove_OutOfRange_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry target = FindStringList(out string prop);
            if (target == null || prop == null) { Console.WriteLine("skip: no string list found"); return; }
            int rc = EbxWriteCommand.ExecuteArrayRemove(
                new[] { target.Name, "--prop", prop, "--index", "999999" }, logger, fmt);
            Assert.AreEqual(1, rc);
        }
    }
}

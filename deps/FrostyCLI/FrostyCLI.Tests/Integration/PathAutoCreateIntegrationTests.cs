using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // Building an inclusion-settings chain from nothing, against real game data.
    //
    //   SubWorldReferenceObjectData.InclusionSettings -> SubWorldInclusionSettings
    //      .Settings[0]                               -> SubWorldInclusionSetting
    //         .Criterion                              -> (external) GameModeInclusionSettings
    //         .EnabledOptions[0]                       = CString
    //
    // Three partition objects joined by internal PointerRefs, one external ref and a string list.
    // The result has to match a shipped one field for field.
    public class PathAutoCreateIntegrationTests
    {
        private const string GameModes = "Levels/MP/Kashyyyk_01/GameModes";

        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        // The whole feature rests on the generated SDK recording what each reference points at. If
        // it ever stopped, auto-creation would have nothing to go on -- and the failure would be a
        // refusal, but it is worth knowing loudly rather than as "cannot create".
        [Test]
        [IntegrationTest("swbf2")]
        public void SwbfTypes_RecordWhatTheirReferencesPointAt()
        {
            EnsureSession(new ExportLogger(quiet: true));

            Type t = TypeLibrary.GetType("SubWorldReferenceObjectData");
            if (t == null) { Console.WriteLine("skip: SubWorldReferenceObjectData not in the type library"); return; }

            EbxFieldMetaAttribute inclusion = Meta(t, "InclusionSettings");
            Assert.IsNotNull(inclusion);
            Assert.AreEqual("Pointer", inclusion.Type.ToString());
            Assert.IsNotNull(inclusion.BaseType);
            Assert.AreEqual("SubWorldInclusionSettings", inclusion.BaseType.Name);

            EbxFieldMetaAttribute settings = Meta(TypeLibrary.GetType("SubWorldInclusionSettings"), "Settings");
            Assert.IsNotNull(settings);
            Assert.AreEqual("Array", settings.Type.ToString());
            Assert.AreEqual("Pointer", settings.ArrayType.ToString());
            Assert.AreEqual("SubWorldInclusionSetting", settings.BaseType.Name);

            EbxFieldMetaAttribute options = Meta(TypeLibrary.GetType("SubWorldInclusionSetting"), "EnabledOptions");
            Assert.IsNotNull(options);
            Assert.AreEqual("Array", options.Type.ToString());
            Assert.AreEqual("CString", options.ArrayType.ToString());
        }

        private static EbxFieldMetaAttribute Meta(Type t, string prop)
        {
            if (t == null) return null;
            PropertyInfo pi = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            return pi == null ? null : (EbxFieldMetaAttribute)Attribute.GetCustomAttribute(pi, typeof(EbxFieldMetaAttribute));
        }

        // The acceptance case: author the whole chain from nothing and match a shipped one.
        [Test]
        [IntegrationTest("swbf2")]
        public void AutoCreate_BuildsAnInclusionChain_MatchingAShippedOne()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }

            // A shipped reference that HAS the chain (the model), and one that does not (the target).
            string modelGuid = null, targetGuid = null;
            foreach (object o in App.AssetManager.GetEbx(entry).Objects)
            {
                if (o == null || o.GetType().Name != "SubWorldReferenceObjectData") continue;
                Guid g = PointerRefUtil.InstanceGuid(o);
                if (g == Guid.Empty) continue;
                object incl = Read(o, "InclusionSettings");
                bool has = PointerRefUtil.RefKind(incl) == "Internal";
                if (has && modelGuid == null) modelGuid = g.ToString();
                else if (!has && targetGuid == null) targetGuid = g.ToString();
            }
            if (modelGuid == null || targetGuid == null)
            { Console.WriteLine("skip: need one reference with an inclusion chain and one without"); return; }

            // What the model says: which criterion asset, and which options.
            object model = Find(App.AssetManager.GetEbx(entry), modelGuid);
            object modelSetting = PointerRefUtil.Target(
                (object)((IList)Read(PointerRefUtil.Target(Read(model, "InclusionSettings")), "Settings"))[0]);
            object modelCriterion = Read(modelSetting, "Criterion");
            Guid criterionFile = ExternalGuid(modelCriterion, "FileGuid");
            Guid criterionClass = ExternalGuid(modelCriterion, "ClassGuid");
            EbxAssetEntry criterionEntry = App.AssetManager.GetEbxEntry(criterionFile);
            if (criterionEntry == null) { Console.WriteLine("skip: criterion asset not resolvable"); return; }
            IList modelOptions = (IList)Read(modelSetting, "EnabledOptions");
            if (modelOptions.Count == 0) { Console.WriteLine("skip: model carries no options"); return; }
            string option = modelOptions[0].ToString();

            try
            {
                // 1. InclusionSettings and its first Settings entry, both created by the path.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteArrayAdd(new[]
                { GameModes, "--object", targetGuid, "--prop", "InclusionSettings.Settings",
                  "--type", "SubWorldInclusionSetting", "--create" }, logger, fmt));

                // 2. The criterion, as an external ref into another asset.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSetRef(new[]
                { GameModes, "--object", targetGuid, "--prop", "InclusionSettings.Settings[0].Criterion",
                  "--target", criterionEntry.Name }, logger, fmt));

                // 3. The gate itself.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteArrayAdd(new[]
                { GameModes, "--object", targetGuid, "--prop", "InclusionSettings.Settings[0].EnabledOptions",
                  "--value", option }, logger, fmt));

                // Now read it back and compare against the shipped chain, hop for hop.
                FrostySdk.IO.EbxAsset reloaded = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(GameModes));
                object built = Find(reloaded, targetGuid);

                object settings = PointerRefUtil.Target(Read(built, "InclusionSettings"));
                Assert.IsNotNull(settings);
                Assert.AreEqual("SubWorldInclusionSettings", settings.GetType().Name);

                IList list = (IList)Read(settings, "Settings");
                Assert.AreEqual(1, list.Count);
                object setting = PointerRefUtil.Target(list[0]);
                Assert.IsNotNull(setting);
                Assert.AreEqual("SubWorldInclusionSetting", setting.GetType().Name);

                object criterion = Read(setting, "Criterion");
                Assert.AreEqual("External", PointerRefUtil.RefKind(criterion));
                Assert.AreEqual(criterionFile, ExternalGuid(criterion, "FileGuid"));
                Assert.AreEqual(criterionClass, ExternalGuid(criterion, "ClassGuid"));

                IList options = (IList)Read(setting, "EnabledOptions");
                Assert.AreEqual(1, options.Count);
                Assert.AreEqual(option, options[0].ToString());

                // The created objects must be real partition members, or serialization drops them and
                // the reference that reaches them addresses a GUID that exists nowhere.
                Assert.IsTrue(InPartition(reloaded, settings), "SubWorldInclusionSettings is not in the partition");
                Assert.IsTrue(InPartition(reloaded, setting), "SubWorldInclusionSetting is not in the partition");
            }
            finally
            {
                EbxWriteCommand.ExecuteRevert(new[] { GameModes }, logger, fmt);
            }
        }

        // Without --create the behaviour must not change: a path through a missing hop is an error,
        // not a silent new object.
        [Test]
        [IntegrationTest("swbf2")]
        public void WithoutCreate_AMissingHop_StillFails()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }

            string targetGuid = null;
            foreach (object o in App.AssetManager.GetEbx(entry).Objects)
            {
                if (o == null || o.GetType().Name != "SubWorldReferenceObjectData") continue;
                Guid g = PointerRefUtil.InstanceGuid(o);
                if (g == Guid.Empty) continue;
                if (PointerRefUtil.RefKind(Read(o, "InclusionSettings")) != "Internal") { targetGuid = g.ToString(); break; }
            }
            if (targetGuid == null) { Console.WriteLine("skip: every reference already has an inclusion chain"); return; }

            int rc = EbxWriteCommand.ExecuteArrayAdd(new[]
            { GameModes, "--object", targetGuid, "--prop", "InclusionSettings.Settings",
              "--type", "SubWorldInclusionSetting" }, logger, fmt);
            Assert.AreEqual(1, rc);
        }

        // ---- helpers ----

        private static bool InPartition(FrostySdk.IO.EbxAsset asset, object obj)
        {
            foreach (object o in asset.Objects) if (ReferenceEquals(o, obj)) return true;
            return false;
        }

        private static object Find(FrostySdk.IO.EbxAsset asset, string guid)
        {
            var want = new Guid(guid);
            foreach (object o in asset.Objects)
                if (o != null && PointerRefUtil.InstanceGuid(o) == want) return o;
            return null;
        }

        private static Guid ExternalGuid(object pointerRef, string member)
        {
            object ext = Read(pointerRef, "External");
            object g = ext == null ? null : Read(ext, member);
            return g is Guid ? (Guid)g : Guid.Empty;
        }

        private static object Read(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }
    }
}

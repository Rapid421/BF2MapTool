using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class IndexServiceTests
    {
        private string _tmpDir;

        private void Setup()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "IndexServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
            IndexService.OverrideCacheDir = _tmpDir;
            IndexService.Reset();
        }

        private void Teardown()
        {
            IndexService.OverrideCacheDir = null;
            IndexService.Reset();
            if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
        }

        [Test]
        public void Catalog_SaveLoad_RoundTrip()
        {
            Setup();
            try
            {
                var records = new List<AssetCatalogRecord>
                {
                    new AssetCatalogRecord { Path = "s2/foo/bar", Type = "SoldierBlueprint", FileGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
                    new AssetCatalogRecord { Path = "s2/foo/baz", Type = "TextureAsset",     FileGuid = "11111111-2222-3333-4444-555555555555" },
                };
                var catalog = new AssetCatalog { GameHash = "testhash", Assets = records };
                IndexService.SaveCatalog(catalog);

                IndexService.Reset();
                var loaded = IndexService.LoadCatalogIfValid("testhash");

                Assert.IsNotNull(loaded);
                Assert.AreEqual(2, loaded.Assets.Count);
                Assert.AreEqual("s2/foo/bar", loaded.Assets[0].Path);
                Assert.AreEqual("SoldierBlueprint", loaded.Assets[0].Type);
                Assert.AreEqual("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", loaded.Assets[0].FileGuid);
            }
            finally { Teardown(); }
        }

        [Test]
        public void Catalog_DegenerateCacheIsRejectedAndNotSaved()
        {
            Setup();
            try
            {
                // A catalog with assets but no real file GUIDs = built without SDK types. It must not
                // be persisted (SaveCatalog drops it), and if one exists on disk it must be rejected.
                var degenerate = new AssetCatalog
                {
                    GameHash = "testhash",
                    Assets = new List<AssetCatalogRecord>
                    {
                        new AssetCatalogRecord { Path = "s2/a", Type = "", FileGuid = "00000000-0000-0000-0000-000000000000" },
                        new AssetCatalogRecord { Path = "s2/b", Type = "", FileGuid = "00000000-0000-0000-0000-000000000000" },
                    }
                };
                Assert.IsTrue(degenerate.IsDegenerate(), "should be flagged degenerate");

                IndexService.SaveCatalog(degenerate);                 // should refuse to write
                Assert.IsFalse(File.Exists(IndexService.GetCatalogPath()), "degenerate catalog must not be cached");

                // Even if a degenerate catalog is on disk, loading rejects it (forces rebuild).
                File.WriteAllText(IndexService.GetCatalogPath(), JsonConvert.SerializeObject(degenerate, Formatting.None));
                Assert.IsNull(IndexService.LoadCatalogIfValid("testhash"));
            }
            finally { Teardown(); }
        }

        [Test]
        public void Catalog_PreVersionCacheIsRejected()
        {
            Setup();
            try
            {
                // A cache file written before the "v" field existed (real guids, matching hash) must
                // be rejected so the version bump actually flushes it.
                string json = "{\"gameHash\":\"testhash\",\"assets\":[" +
                    "{\"path\":\"s2/a\",\"type\":\"TextureAsset\",\"fileGuid\":\"11111111-2222-3333-4444-555555555555\"}]}";
                File.WriteAllText(IndexService.GetCatalogPath(), json);
                Assert.IsNull(IndexService.LoadCatalogIfValid("testhash"));
            }
            finally { Teardown(); }
        }

        [Test]
        public void Catalog_InvalidatedWhenHashChanges()
        {
            Setup();
            try
            {
                var catalog = new AssetCatalog { GameHash = "oldhash", Assets = new List<AssetCatalogRecord>() };
                IndexService.SaveCatalog(catalog);

                var loaded = IndexService.LoadCatalogIfValid("newhash");
                Assert.IsNull(loaded);
            }
            finally { Teardown(); }
        }

        [Test]
        public void RefIndex_SaveLoad_RoundTrip()
        {
            Setup();
            try
            {
                var index = new RefIndex { GameHash = "testhash" };
                index.Refs["s2/foo/target"] = new List<string> { "s2/foo/sourceA", "s2/foo/sourceB" };
                IndexService.SaveRefIndex(index);

                IndexService.Reset();
                var loaded = IndexService.LoadRefIndexIfValid("testhash");

                Assert.IsNotNull(loaded);
                Assert.IsTrue(loaded.Refs.ContainsKey("s2/foo/target"));
                Assert.AreEqual(2, loaded.Refs["s2/foo/target"].Count);
                Assert.AreEqual("s2/foo/sourceA", loaded.Refs["s2/foo/target"][0]);
            }
            finally { Teardown(); }
        }

        [Test]
        public void RefIndex_InvalidatedWhenHashChanges()
        {
            Setup();
            try
            {
                var index = new RefIndex { GameHash = "oldhash" };
                IndexService.SaveRefIndex(index);

                Assert.IsNull(IndexService.LoadRefIndexIfValid("newhash"));
            }
            finally { Teardown(); }
        }

        [Test]
        public void MergeRecords_OverrideReplaces_NewAppends()
        {
            var baseRecords = new List<AssetCatalogRecord>
            {
                new AssetCatalogRecord { Path = "s2/foo", Type = "OldType", FileGuid = "aaaaaaaa-0000-0000-0000-000000000000" },
                new AssetCatalogRecord { Path = "s2/bar", Type = "BarType", FileGuid = "bbbbbbbb-0000-0000-0000-000000000000" },
            };
            var overrides = new List<AssetCatalogRecord>
            {
                new AssetCatalogRecord { Path = "s2/foo",     Type = "NewType", FileGuid = "cccccccc-0000-0000-0000-000000000000" },
                new AssetCatalogRecord { Path = "s2/newasset",Type = "FooType", FileGuid = "dddddddd-0000-0000-0000-000000000000" },
            };

            var merged = IndexService.MergeRecords(baseRecords, overrides);

            Assert.AreEqual(3, merged.Count);
            Assert.AreEqual("NewType", merged[0].Type);
            Assert.AreEqual("BarType", merged[1].Type);
            Assert.AreEqual("s2/newasset", merged[2].Path);
        }

        [Test]
        public void SaveCatalog_DoesNotInclude_MarkedProjectAssets()
        {
            Setup();
            try
            {
                var records = new List<AssetCatalogRecord>
                {
                    new AssetCatalogRecord { Path = "s2/base", Type = "BaseType", FileGuid = "aaaaaaaa-0000-0000-0000-000000000000" }
                };
                var catalog = new AssetCatalog { GameHash = "h", Assets = records };
                IndexService.SaveCatalog(catalog);

                string json = File.ReadAllText(Path.Combine(_tmpDir, "catalog.json"));
                Assert.IsFalse(json.Contains("ProjectAsset"));
            }
            finally { Teardown(); }
        }

        [Test]
        public void CatalogPath_IsUnder_OverrideCacheDir()
        {
            Setup();
            try
            {
                string path = IndexService.GetCatalogPath();
                Assert.IsTrue(path.StartsWith(_tmpDir));
            }
            finally { Teardown(); }
        }
    }
}

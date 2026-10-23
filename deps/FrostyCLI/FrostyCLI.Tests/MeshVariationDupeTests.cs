using System;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class MeshVariationDupeTests
    {
        [Test]
        public void NameHash_MatchesShippedObjectVariation()
        {
            // shipped: S2/.../BespinContainer_01_A_AdministratorPalace carries NameHash 2640686357 and Cloud City's
            // InstanceObjectVariation lists that value for all 56 containers
            Assert.AreEqual(2640686357u, MeshVariationDupe.NameHash("S2/Objects/Props/ObjectSets/Bespin/BespinContainer_01_A/BespinContainer_01_A_AdministratorPalace"));
            Assert.AreEqual(MeshVariationDupe.NameHash("A/B/Some_Variation"), MeshVariationDupe.NameHash("a/b/some_variation"));
        }

        [Test]
        public void ExplainLod_RecoversTheLodOfAnEntryHash()
        {
            uint mesh = 1256927881, variation = 2640686357;
            for (int lod = 0; lod < 6; lod++)
                Assert.AreEqual(lod, MeshVariationDupe.ExplainLod(MeshVariationDupe.EntryHash(mesh, variation, lod), mesh, variation, 32));
            Assert.AreEqual(-1, MeshVariationDupe.ExplainLod(MeshVariationDupe.EntryHash(mesh, variation, 3), mesh, variation + 1, 32));
        }

        [Test]
        public void EntryHash_DependsOnVariation()
        {
            Assert.IsTrue(MeshVariationDupe.EntryHash(1, 2, 0) != MeshVariationDupe.EntryHash(1, 3, 0));
            Assert.IsTrue(MeshVariationDupe.EntryHash(1, 2, 0) != MeshVariationDupe.EntryHash(1, 2, 1));
        }

        [Test]
        public void SaltedHash_IsDeterministicAndChangesTheHash()
        {
            byte[] salt = Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray();
            ulong a = MeshVariationDupe.SaltedHash(42, salt), b = MeshVariationDupe.SaltedHash(42, salt);
            Assert.AreEqual(a, b);
            Assert.IsTrue(a != 42);
            Assert.IsTrue(a != MeshVariationDupe.SaltedHash(42, Guid.Empty.ToByteArray()));
        }

        [Test]
        public void MeshFileName_IsTheLowercasedFileName()
        {
            Assert.AreEqual("bespincontainer_01_a_mesh", MeshVariationDupe.MeshFileName("Objects/Props/ObjectSets/Bespin/BespinContainer_01_A/BespinContainer_01_A_Mesh"));
        }

        [Test]
        public void DeterministicGuid_IsStablePerKey()
        {
            Assert.AreEqual(MeshVariationDupe.DeterministicGuid("x"), MeshVariationDupe.DeterministicGuid("x"));
            Assert.IsTrue(MeshVariationDupe.DeterministicGuid("x") != MeshVariationDupe.DeterministicGuid("y"));
        }

        [Test]
        public void TryParseVector_PadsToFourAndRejectsJunk()
        {
            float[] v;
            Assert.IsTrue(MeshVariationDupe.TryParseVector("1,0.5,0.25", out v));
            Assert.AreEqual(4, v.Length);
            Assert.AreEqual(0.25f, v[2]);
            Assert.AreEqual(0f, v[3]);
            Assert.IsFalse(MeshVariationDupe.TryParseVector("1,2,3,4,5", out v));
            Assert.IsFalse(MeshVariationDupe.TryParseVector("red", out v));
        }
    }
}

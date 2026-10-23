using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class MaterialIdsTests
    {
        [Test]
        public void Derive_IsDeterministicAndNonZero()
        {
            Guid g = new Guid("3f1487c4-82b5-73fb-0eb1-757b618a2836");
            long a = MaterialIds.Derive(g, Guid.Empty);
            long b = MaterialIds.Derive(g, Guid.Empty);
            Assert.AreEqual(a, b);
            Assert.IsTrue(a != 0);
        }

        [Test]
        public void Derive_DistinguishesMaterialsAndVariations()
        {
            Guid g1 = new Guid("3f1487c4-82b5-73fb-0eb1-757b618a2836");
            Guid g2 = new Guid("94673953-8d15-ad5b-7ed3-341949574916");
            Assert.IsTrue(MaterialIds.Derive(g1, Guid.Empty) != MaterialIds.Derive(g2, Guid.Empty));
            Assert.IsTrue(MaterialIds.Derive(g1, Guid.Empty) != MaterialIds.Derive(g1, g2));
        }
    }
}

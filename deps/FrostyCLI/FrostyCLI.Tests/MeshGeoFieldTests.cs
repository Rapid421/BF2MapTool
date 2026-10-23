using System;
using System.Collections.Generic;
using System.IO;
using FrostySdk.IO;
using FrostyCLI.Adapters.Mesh;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Tests
{
    // The geometry port's material holder: a tessellated section's depot holder carries the MeshMaterial's own fields
    // (triangle size, max distance, cull epsilon, shape factor, displacement scale/bias) as Float32 entries. Stripping them
    // as donor overrides left the Hoth 2015 rock-wall ports with none and the GPU faulted near them (HOTH_HX_A1b,
    // DXGI_ERROR_DEVICE_REMOVED); they are now kept and set to the source material's values.
    public class MeshGeoFieldTests
    {
        private static ParameterEntry Entry(uint nameHash, uint typeHash, byte[] value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(((ulong)(nameHash & 0xFFFF) << 48) | 0x1234UL);   // parameterHash: low 16 bits of the name in its top bits
                w.Write(typeHash);
                w.Write((ushort)1);
                w.Write((ushort)(nameHash >> 16));
                w.Write(value.Length);
                w.Write(value);
                w.Flush();
                ms.Position = 0;
                using (var r = new NativeReader(ms)) return new ParameterEntry(r);
            }
        }

        [Test]
        public void FieldEntriesAreFloatIntAndBool()
        {
            Assert.IsTrue(MeshGeoPort.IsMaterialFieldEntry(MeshGeoPort.TypeFloat32));
            Assert.IsTrue(MeshGeoPort.IsMaterialFieldEntry(MeshGeoPort.TypeInt64));
            Assert.IsTrue(MeshGeoPort.IsMaterialFieldEntry(MeshGeoPort.TypeBoolean));
            Assert.IsFalse(MeshGeoPort.IsMaterialFieldEntry(193460885u));    // Vec: an EBX VectorParameter
            Assert.IsFalse(MeshGeoPort.IsMaterialFieldEntry(2903162835u));   // ITexture
        }

        [Test]
        public void SourceTessellationFieldsReplaceTheDonors()
        {
            var tri = Entry(0x0DCC3660u, MeshGeoPort.TypeFloat32, BitConverter.GetBytes(20f));
            var dist = Entry(0x5448AEFAu, MeshGeoPort.TypeFloat32, BitConverter.GetBytes(7f));
            var flag = Entry(0x025960ACu, MeshGeoPort.TypeBoolean, new byte[] { 1 });
            Assert.AreEqual(0x0DCC3660u, tri.NameHash);
            var pb = new ShaderPersistentParamDbBlock();
            pb.Parameters.Add(tri); pb.Parameters.Add(dist); pb.Parameters.Add(flag);
            var holder = new ShaderStaticParamDbBlock();
            holder.Resources.Add(pb);
            int set = MeshGeoPort.ApplyMaterialFields(holder, new Dictionary<string, string>
                { { "TessellationTriangleSize", "12" }, { "TessellationMaxDistance", "20" }, { "CastShadow", "False" } });
            Assert.AreEqual(2, set);
            Assert.AreEqual(12f, BitConverter.ToSingle(tri.Value, 0));
            Assert.AreEqual(20f, BitConverter.ToSingle(dist.Value, 0));
            Assert.AreEqual((byte)1, flag.Value[0]);   // unmapped Boolean keeps the donor's value
        }
    }
}

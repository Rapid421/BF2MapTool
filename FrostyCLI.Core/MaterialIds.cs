using System;

namespace FrostyCLI.Core
{
    /// <summary>
    /// MeshVariationDatabaseMaterial.MaterialId is a 64-bit identity of a (material, variation)
    /// pair. Shipped data shows it is global and stable -- the same material carries the same id in
    /// every level's database -- but it is not derivable from cooked data (no hash of the material
    /// GUID, mesh path, shader or texture parameters reproduces it) and nothing outside the
    /// variation database references it. Shipped community mods register hundreds of materials with
    /// id 0 and render, so the runtime does not need the authored value. New records get a
    /// deterministic, unique id instead of 0 so that any runtime map keyed on it cannot collide.
    /// </summary>
    public static class MaterialIds
    {
        private const ulong Basis = 0xcbf29ce484222325UL;
        private const ulong Prime = 0x100000001b3UL;

        public static long Derive(Guid material, Guid variation)
        {
            ulong h = Basis;
            foreach (byte b in material.ToByteArray()) { h ^= b; h *= Prime; }
            if (variation != Guid.Empty)
                foreach (byte b in variation.ToByteArray()) { h ^= b; h *= Prime; }
            if (h == 0) h = Basis;
            return unchecked((long)h);
        }
    }
}

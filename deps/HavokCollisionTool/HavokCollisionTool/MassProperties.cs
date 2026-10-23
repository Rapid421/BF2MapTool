using System.Numerics;
using hk;
using HavokFb;

namespace HavokCollisionTool;

/// <summary>
/// Mass properties for a rebuilt shape.
///
/// Volume, mass and centre of mass are plain floats and are computed exactly from
/// the mesh. The three <c>short[4]</c> members of <see cref="hkCompressedMassProperties"/>
/// are a quantised inertia frame whose encoding is NOT established by any source in
/// the research corpus, so this deliberately does not invent one: the quantised
/// fields are carried over verbatim from the shape being replaced.
///
/// Consequence: a replaced *dynamic* body keeps the inertia tensor of the shape it
/// replaced, so its rotational response will not match a drastically different mesh.
/// Static level collision — the normal use for this tool — does not read them.
/// </summary>
internal static class MassProperties
{
    /// <summary>Density used when a shape is built with no template to inherit from.</summary>
    private const float DefaultDensity = 1.0f;

    public static hkRefCountedProperties? Build(WavefrontObj objFile, hknpShape? template)
    {
        (float volume, Vector3 centre) = ComputeVolumeAndCentroid(objFile);

        hkCompressedMassProperties? source = FindCompressed(template);

        hkCompressedMassProperties compressed = new()
        {
            _volume = volume,
            _mass = source is not null && source._volume > 0f
                // Preserve the original effective density across the geometry change.
                ? volume * (source._mass / source._volume)
                : volume * DefaultDensity,
            _centerOfMass = source?._centerOfMass.ToArray() ?? new short[4],
            _inertia = source?._inertia.ToArray() ?? new short[4],
            _majorAxisSpace = source?._majorAxisSpace.ToArray() ?? new short[4]
        };

        if (source is null)
        {
            // No template: leave an identity-ish frame rather than a copied constant
            // from an unrelated asset. Flagged to the caller by Program.cs.
            compressed._majorAxisSpace = new short[] { short.MinValue, short.MinValue, short.MinValue, 0 };
        }

        hknpShapeMassProperties massProperties = new() { _compressedMassProperties = compressed };

        return new hkRefCountedProperties
        {
            _entries = new List<hkRefCountedPropertiesEntry?>
            {
                new()
                {
                    _object = massProperties,
                    _flags = 0,
                    _key = MassPropertiesKey
                }
            }
        };
    }

    /// <summary>
    /// The key a shape's mass properties are filed under: 979 of 979 shipped property entries
    /// use 0xF100.
    ///
    /// Earlier versions wrote 61698 (0xF102) on every shape they built. The value came from
    /// upstream HavokFb's builder, carried over under a comment claiming it had been observed on
    /// stock shapes; it had not. Nothing structural notices — the res loads and collides — but
    /// anything that asks a shape for its mass properties looks up 0xF100 and finds none.
    /// </summary>
    public const ushort MassPropertiesKey = 0xF100;

    /// <summary>
    /// Refiles mass properties an earlier build stored under the wrong key, for a shape and every
    /// shape beneath it. An import rewrites every slot, so without this a shape from an earlier
    /// import in an untouched slot would keep the wrong key.
    /// </summary>
    /// <returns>How many entries were refiled.</returns>
    public static int RepairKeys(hknpShape? shape) =>
        RepairKeys(shape, new HashSet<hknpShape>(ReferenceEqualityComparer.Instance));

    private static int RepairKeys(hknpShape? shape, HashSet<hknpShape> seen)
    {
        if (shape is null || !seen.Add(shape))
        {
            return 0;
        }

        int refiled = 0;

        if (shape._properties?._entries is { } entries)
        {
            foreach (hkRefCountedPropertiesEntry? entry in entries)
            {
                if (entry?._object is hknpShapeMassProperties && entry._key != MassPropertiesKey)
                {
                    entry._key = MassPropertiesKey;
                    refiled++;
                }
            }
        }

        if (shape is hknpCompoundShape compound && compound._instances is not null)
        {
            foreach (hknpShapeInstance? instance in compound._instances._elements)
            {
                refiled += RepairKeys(instance?._shape, seen);
            }
        }

        return refiled;
    }

    public static bool HasTemplateProperties(hknpShape? template) => FindCompressed(template) is not null;

    private static hkCompressedMassProperties? FindCompressed(hknpShape? shape)
    {
        if (shape?._properties?._entries is null)
        {
            return null;
        }

        foreach (hkRefCountedPropertiesEntry? entry in shape._properties._entries)
        {
            if (entry?._object is hknpShapeMassProperties mass && mass._compressedMassProperties is not null)
            {
                return mass._compressedMassProperties;
            }
        }

        return null;
    }

    /// <summary>
    /// Signed-tetrahedron volume integral over the closed triangulated mesh, and the
    /// volume centroid. An open mesh gives a meaningless (possibly negative) volume,
    /// so the result is clamped and the caller warns.
    /// </summary>
    public static (float Volume, Vector3 Centroid) ComputeVolumeAndCentroid(WavefrontObj objFile)
    {
        double volume = 0.0;
        Vector3 accum = Vector3.Zero;

        foreach (WavefrontObj.Face face in objFile._faces)
        {
            List<int> idx = face._indices;
            // Fan-triangulate n-gons.
            for (int i = 1; i < idx.Count - 1; i++)
            {
                Vector3 a = ToVec3(objFile._vertices[idx[0]]);
                Vector3 b = ToVec3(objFile._vertices[idx[i]]);
                Vector3 c = ToVec3(objFile._vertices[idx[i + 1]]);

                float signedVolume = Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0f;
                volume += signedVolume;
                accum += (a + b + c) * (signedVolume / 4.0f);
            }
        }

        float v = (float)Math.Abs(volume);
        Vector3 centroid = v > 1e-9f ? accum / (float)volume : Vector3.Zero;
        return (v, centroid);
    }

    private static Vector3 ToVec3(Vector4 v) => new(v.X, v.Y, v.Z);
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FrostyCLI.Core
{
    // An asset a light points at -- its IES profile, or the texture of a rectangular light. Recorded
    // by file GUID rather than by path: the two games ship much of the Bespin content unchanged under
    // identical GUIDs (all four of 2015's T_IES_Hotel profiles resolve in Battlefront II), so a GUID
    // is what lets the importer recognise an asset the target already has. The path and type are
    // carried for reporting and as a fallback when the target renamed but kept the asset.
    public class LightAssetRef
    {
        public string Field;
        public string Path;
        public string FileGuid;
        public string ClassGuid;
        public string Type;
    }

    // One light entity captured by 'level export-lights' and replayed by 'level import-lights'.
    // Transforms are world space, already composed through any prefab / blueprint instancing.
    public class LightRecord
    {
        public string Type;
        public string Source;
        /// <summary>For *ReferenceObjectData (effects, prefabs), the asset it points at. Null for entities.</summary>
        public string Blueprint;
        public float[] Right;
        public float[] Up;
        public float[] Forward;
        public float[] Trans;
        public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<LightAssetRef> Refs = new List<LightAssetRef>();
    }

    public static class LightPort
    {
        // Engine-managed or game-specific members that must never be copied between games. Flags is
        // an opaque per-build bitfield; the transform is written separately from the record's matrix.
        public static readonly HashSet<string> SkipOnImport = new HashSet<string>(StringComparer.Ordinal)
        {
            "Flags", "Realm", "Transform", "BlueprintTransform"
        };

        public static bool IsLightEntityType(string typeName)
        {
            return typeName != null && typeName.EndsWith("LightEntityData", StringComparison.Ordinal);
        }

        /// <summary>
        /// The other per-instance lighting entities a level carries beside its lights: reflection
        /// volumes (box / sphere / distant), Enlighten light-probe volumes and lens flares. They are
        /// captured exactly like lights -- world transform plus every scalar field -- so one walk can
        /// port a level's whole lighting set instead of just the emitters.
        /// </summary>
        public static bool IsLightingEntityType(string typeName)
        {
            if (typeName == null) return false;
            return IsLightEntityType(typeName)
                || typeName.EndsWith("ReflectionVolumeEntityData", StringComparison.Ordinal)
                || typeName == "LightProbeVolumeData"
                || typeName == "LensFlareEntityData";
        }

        /// <summary>Type filter for a walk: null/empty selects <see cref="IsLightingEntityType"/>.</summary>
        public static Func<string, bool> TypeFilter(string csvTypes)
        {
            if (string.IsNullOrWhiteSpace(csvTypes)) return IsLightingEntityType;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in csvTypes.Split(','))
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            // "lights" keeps the original emitter-only behaviour for callers that want it
            if (set.Count == 1 && set.Contains("lights")) return IsLightEntityType;
            return n => n != null && set.Contains(n);
        }

        public static LinearTransformData Identity()
        {
            return new LinearTransformData
            {
                Right = new[] { 1f, 0f, 0f },
                Up = new[] { 0f, 1f, 0f },
                Forward = new[] { 0f, 0f, 1f },
                Trans = new[] { 0f, 0f, 0f }
            };
        }

        // Frostbite LinearTransforms are row vectors on a row-major basis: world = local * parent.
        // The basis rows carry scale, so composing scaled prefabs needs no special case.
        public static LinearTransformData Compose(LinearTransformData local, LinearTransformData parent)
        {
            float[][] basis = { Row(parent.Right, 0), Row(parent.Up, 1), Row(parent.Forward, 2) };
            float[] trans = parent.Trans ?? new float[3];
            return new LinearTransformData
            {
                Right = MulRow(Row(local.Right, 0), basis),
                Up = MulRow(Row(local.Up, 1), basis),
                Forward = MulRow(Row(local.Forward, 2), basis),
                Trans = Add(MulRow(local.Trans ?? new float[3], basis), trans)
            };
        }

        public static bool TryParseVec3(string text, out float[] value)
        {
            value = null;
            if (text == null) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;
            float[] result = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return false;
            }
            value = result;
            return true;
        }

        public static string FormatVec3(float[] v)
        {
            return v[0].ToString("R", CultureInfo.InvariantCulture) + "," +
                   v[1].ToString("R", CultureInfo.InvariantCulture) + "," +
                   v[2].ToString("R", CultureInfo.InvariantCulture);
        }

        public static readonly string[] VectorAxes = { "x", "y", "z", "w" };

        // Vec2 / Vec3 / Vec4 as "x,y[,z[,w]]": the text form lens-flare element curves and sizes travel in.
        public static bool TryParseVector(string text, out float[] value)
        {
            value = null;
            if (text == null) return false;
            string[] parts = text.Split(',');
            if (parts.Length < 2 || parts.Length > 4) return false;
            float[] result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return false;
            }
            value = result;
            return true;
        }

        public static string FormatVector(float[] v)
        {
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++) parts[i] = v[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        // Multiplies a numeric field value by a factor, keeping the invariant text form the
        // property writer expects. Non-numeric input is returned unchanged.
        public static string ScaleValue(string value, float factor)
        {
            double parsed;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return value;
            return ((float)(parsed * factor)).ToString("R", CultureInfo.InvariantCulture);
        }

        private static float[] Row(float[] v, int axis)
        {
            if (v != null && v.Length == 3) return v;
            float[] unit = new float[3];
            unit[axis] = 1f;
            return unit;
        }

        private static float[] MulRow(float[] v, float[][] m)
        {
            float[] r = new float[3];
            for (int j = 0; j < 3; j++)
                r[j] = v[0] * m[0][j] + v[1] * m[1][j] + v[2] * m[2][j];
            return r;
        }

        private static float[] Add(float[] a, float[] b)
        {
            return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        }
    }
}

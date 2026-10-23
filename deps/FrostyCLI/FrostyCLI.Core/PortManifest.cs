using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace FrostyCLI.Core
{
    // What `port export` writes next to the exported files and `port import` consumes. Paths in
    // File fields are relative to the package directory. GUIDs are kept as strings so the manifest
    // is readable and diffable.
    public class PortManifest
    {
        public int Version = 1;
        public string SourceGame;
        public string Blueprint;
        public string BlueprintGuid;
        public string BlueprintFile;     // deep EBX dump of the source blueprint (reference only)
        public string EntityType;
        public PortMesh Mesh;
        public PortPhysics Physics;
        public List<PortBox> PartBoundingBoxes = new List<PortBox>();
        public List<string> Notes = new List<string>();

        public static PortManifest Load(string dir)
        {
            string file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) throw new FileNotFoundException("no manifest.json in " + dir);
            return JsonConvert.DeserializeObject<PortManifest>(File.ReadAllText(file));
        }

        public void Save(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    public class PortMesh
    {
        public string Path;
        public string Guid;
        public string Type;
        public string File;              // FBX
        public int LodCount;
        public List<List<string>> SectionNames = new List<List<string>>();
        public List<List<int>> SectionMaterials = new List<List<int>>();
        public List<PortMaterial> Materials = new List<PortMaterial>();
        public string GeoFile;           // MESHGEO1 dump of every LOD/section (mesh geo-dump); null for old packages
        public Dictionary<string, string> Fields = new Dictionary<string, string>();   // mesh root scalar fields (LodGroup = asset path)
        // The source's OccluderMeshResource, verbatim (res type OccluderMesh: the same 0x40-byte header -- bounds, vertex
        // and index counts, offsets -- in SWBF 2015 and BFII). Null when the source has none or the package predates it:
        // a mesh whose OccluderMeshEnable is set without a resource crashes on creation (game+0x6E6ED6A).
        public string OccluderFile;
        public string OccluderResMeta;   // hex
        public uint OccluderResType;
    }

    public class PortMaterial
    {
        public int Index;
        public string ClassGuid;
        public string ShaderPath;
        public string ShaderGuid;
        public List<PortTexture> Textures = new List<PortTexture>();
        public List<PortVector> Vectors = new List<PortVector>();
        public List<PortBool> Bools = new List<PortBool>();
        public List<PortConditional> Conditionals = new List<PortConditional>();
        public Dictionary<string, string> Fields = new Dictionary<string, string>();   // MeshMaterial scalar fields (CastShadow ...)
        public bool ParamsExported;      // false for packages written before Vectors/Bools/Conditionals were exported
        public PortTexture DisplacementMap;   // MeshMaterial.DisplacementMap (tessellated materials); null when unset or not exported
    }

    public class PortVector
    {
        public string Param;
        public string Type;              // ShaderParameterType_Scalar / _Vec2 / _Vec3 / _Vec4 / _Color
        public float[] Value;
    }

    public class PortBool
    {
        public string Param;
        public bool Value;
    }

    public class PortConditional
    {
        public string AssetPath;
        public string AssetGuid;
        public string Value;
    }

    public class PortTexture
    {
        public string Param;
        public string Path;
        public string Guid;
        public string File;              // DDS
        public string Source;            // "shader-default": the graph's own texture for a parameter the material leaves unset
    }

    public class PortPhysics
    {
        public string AssetPath;
        public string Guid;
        public string ResName;
        public string File;              // raw decompressed res payload
        public uint ResType;
        public string MetaHex;
    }

    public class PortBox
    {
        public float[] Min;
        public float[] Max;
    }

    // What `port export-layer` writes: every static placement of a level (or one of its groups) with
    // the blueprint it uses and the full transform, so `port import-layer` can rebuild the layout in
    // the other game.
    public class PortLayer
    {
        public int Version = 1;
        public string SourceGame;
        public string Level;
        public List<PortPlacement> Placements = new List<PortPlacement>();

        public static PortLayer Load(string dir)
        {
            string file = Path.Combine(dir, "layer.json");
            if (!File.Exists(file)) throw new FileNotFoundException("no layer.json in " + dir);
            return JsonConvert.DeserializeObject<PortLayer>(File.ReadAllText(file));
        }

        public void Save(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "layer.json"), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    public class PortPlacement
    {
        public string Blueprint;
        public string Guid;
        public int Group;
        public int Member;
        public int Instance;
        public string Source;            // "ebx" or "havok"
        public float[] Right;
        public float[] Up;
        public float[] Forward;
        public float[] Trans;
    }
}

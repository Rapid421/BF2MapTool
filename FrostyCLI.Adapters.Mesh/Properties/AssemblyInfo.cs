using System.Reflection;
using System.Runtime.InteropServices;
using Frosty.Core.Attributes;
using FrostyCLI.Adapters.Mesh;

[assembly: AssemblyTitle("FrostyCLI.Adapters.Mesh")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

[assembly: RegisterAssetDefinition("RigidMeshAsset",     typeof(HeadlessMeshAssetDefinition))]
[assembly: RegisterAssetDefinition("SkinnedMeshAsset",   typeof(HeadlessMeshAssetDefinition))]
[assembly: RegisterAssetDefinition("CompositeMeshAsset", typeof(HeadlessMeshAssetDefinition))]

using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using Frosty.Core.Attributes;
using FrostyCLI.Adapters.Texture;

[assembly: AssemblyTitle("FrostyCLI.Adapters.Texture")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

[assembly: RegisterAssetDefinition("TextureAsset",     typeof(HeadlessTextureAssetDefinition))]
[assembly: RegisterAssetDefinition("TextureBaseAsset", typeof(HeadlessTextureAssetDefinition))]
// UITextureAsset wraps a Texture via the same `.Resource` RID, so the texture exporter handles it as-is.
[assembly: RegisterAssetDefinition("UITextureAsset",   typeof(HeadlessTextureAssetDefinition))]

[assembly: InternalsVisibleTo("FrostyCLI.Tests")]
// AtlasTextureAsset (particle atlases) wraps its Texture the same way (`.Resource`); the frame layout lives in the EBX
// (AnimationFrameCount/AnimationColumnCount), so the texture handler exports and imports its pixels unchanged.
[assembly: RegisterAssetDefinition("AtlasTextureAsset", typeof(HeadlessTextureAssetDefinition))]

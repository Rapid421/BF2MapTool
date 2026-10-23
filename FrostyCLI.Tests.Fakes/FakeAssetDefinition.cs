using Frosty.Core;
using Frosty.Core.Attributes;

[assembly: RegisterAssetDefinition("FakeAsset", typeof(FrostyCLI.Tests.Fakes.FakeAssetDefinition))]
[assembly: RegisterAssetDefinition("AnotherFakeAsset", typeof(FrostyCLI.Tests.Fakes.FakeAssetDefinition))]

namespace FrostyCLI.Tests.Fakes
{
    public class FakeAssetDefinition : AssetDefinition
    {
        public FakeAssetDefinition() { }
    }
}

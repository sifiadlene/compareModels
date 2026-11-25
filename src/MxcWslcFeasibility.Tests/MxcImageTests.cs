using System.Formats.Tar;
using System.Text;
using Xunit;

namespace MxcWslcFeasibility.Tests;

public sealed class MxcImageTests
{
    [Fact]
    public void GivenUnselectedExtraConfig_WhenValidated_ItIsNotParsedAsThePinnedConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mxc-image-{Guid.NewGuid():N}.tar");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new TarWriter(stream))
            {
                string manifest = $$"""
                    [{"Config":"{{MxcImage.ConfigSha256}}.json","RepoTags":["{{MxcImage.Name}}"],"Layers":[]}]
                    """;
                AddFile(writer, "manifest.json", manifest);
                AddFile(writer, $"extra/{MxcImage.ConfigSha256}", "not a selected config");
            }

            Assert.Throws<IOException>(() => MxcImage.Validate(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AddFile(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
        };
        writer.WriteEntry(entry);
        entry.DataStream.Dispose();
    }
}

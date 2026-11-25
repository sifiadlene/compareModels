using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MxcWslcFeasibility.Tests;

public sealed class MxcImageConfigValidationTests
{
    // Exact bytes from the pinned Docker-save configuration, including its trailing newline.
    private const string PinnedConfig = """
        {"config":{"Env":["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],"Entrypoint":[],"Cmd":["bash"]},"created":"2026-11-04T21:23:39Z","history":[{"created":"2026-11-04T21:23:39Z","created_by":"# debian.sh --arch 'amd64' out/ 'bookworm' '@1793827419'","comment":"debuerreotype 0.17"}],"rootfs":{"type":"layers","diff_ids":["sha256:1d7b9dd8778ebc4d9bf2f64436dd358640718cb349f5640b33bebaea4ffd62c2"]},"os":"linux","architecture":"amd64"}
        """ + "\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenValidArchive_WhenValidated_AcceptsEitherEntryOrder(bool manifestFirst)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"mxc-config-test-{Guid.NewGuid():N}.tar");
        try
        {
            const string layer = "valid layer";
            string layerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(layer)));
            string config = JsonSerializer.Serialize(new
            {
                os = "linux",
                architecture = "amd64",
                rootfs = new { diff_ids = new[] { $"sha256:{layerHash}" } }
            });
            string configHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
            string configPath = $"blobs/sha256/{configHash}";
            string manifest = JsonSerializer.Serialize(new[]
            {
                new { Config = configPath, RepoTags = new[] { MxcImage.Name }, Layers = new[] { "layer.tar" } }
            });

            using (var stream = File.Create(path))
            using (var writer = new TarWriter(stream))
            {
                if (manifestFirst) AddFile(writer, "manifest.json", manifest);
                AddFile(writer, configPath, config);
                AddFile(writer, "layer.tar", layer);
                if (!manifestFirst) AddFile(writer, "manifest.json", manifest);
            }

            string archiveHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(archiveHash, MxcImage.Validate(path, configHash));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenForgedExtraConfig_WhenValidated_RejectsModifiedLayers(bool manifestFirst)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"mxc-config-test-{Guid.NewGuid():N}.tar");
        try
        {
            string configPath = $"blobs/sha256/{MxcImage.ConfigSha256}";
            const string layer = "modified layer";
            string layerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(layer)));
            string manifest = JsonSerializer.Serialize(new[]
            {
                new { Config = configPath, RepoTags = new[] { MxcImage.Name }, Layers = new[] { "layer.tar" } }
            });
            string forgedConfig = JsonSerializer.Serialize(new
            {
                os = "linux",
                architecture = "amd64",
                rootfs = new { diff_ids = new[] { $"sha256:{layerHash}" } }
            });

            using (var stream = File.Create(path))
            using (var writer = new TarWriter(stream))
            {
                if (manifestFirst) AddFile(writer, "manifest.json", manifest);
                AddFile(writer, configPath, PinnedConfig);
                AddFile(writer, $"extra/{MxcImage.ConfigSha256}", forgedConfig);
                AddFile(writer, "layer.tar", layer);
                if (!manifestFirst) AddFile(writer, "manifest.json", manifest);
            }

            IOException error = Assert.Throws<IOException>(() => MxcImage.Validate(path));
            Assert.Equal("The image layers do not match the pinned configuration.", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenForgedSelectedConfigAndGenuineExtraConfig_WhenValidated_RejectsImage(bool manifestFirst)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"mxc-config-test-{Guid.NewGuid():N}.tar");
        try
        {
            string configPath = $"blobs/sha256/{MxcImage.ConfigSha256}";
            string manifest = JsonSerializer.Serialize(new[]
            {
                new { Config = configPath, RepoTags = new[] { MxcImage.Name }, Layers = Array.Empty<string>() }
            });
            using (var stream = File.Create(path))
            using (var writer = new TarWriter(stream))
            {
                if (manifestFirst) AddFile(writer, "manifest.json", manifest);
                AddFile(writer, configPath, """{"os":"linux","architecture":"amd64","rootfs":{"diff_ids":[]}}""");
                AddFile(writer, $"extra/{MxcImage.ConfigSha256}", PinnedConfig);
                if (!manifestFirst) AddFile(writer, "manifest.json", manifest);
            }

            IOException error = Assert.Throws<IOException>(() => MxcImage.Validate(path));
            Assert.Equal("The image identity, tag, or architecture does not match the pinned baseline.", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AddFile(TarWriter writer, string name, string content)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
    }
}

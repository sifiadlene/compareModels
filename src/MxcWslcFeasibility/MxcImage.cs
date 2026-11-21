using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using static WslContainerFeasibility.ContainerOperations;

namespace MxcWslcFeasibility;

internal static class MxcImage
{
    internal const string Name = "comparemodels-mxc-evaluation:734a72b3";
    internal const string ConfigSha256 = "43f84a0b210f98845ef003912981774c95efe439eaa4b1cb62cf803fd46fc82a";
    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "compareModels", "Feasibility", "MxcWslc", "image");
    internal static string ArchivePath => Path.Combine(DirectoryPath, "image.tar");

    internal static async Task<string> PrepareAsync(CancellationToken cancellationToken)
    {
        var prerequisites = new WslFixtureRunner().CheckPrerequisites();
        if (!prerequisites.Ready)
        {
            throw new InvalidOperationException(prerequisites.Details);
        }
        if (File.Exists(ArchivePath))
        {
            return Validate(ArchivePath);
        }

        Directory.CreateDirectory(DirectoryPath);
        string id = Guid.NewGuid().ToString("N");
        string sessionName = $"CompareModels-MxcImage-{id}";
        string storage = Path.Combine(DirectoryPath, $"store-{id}");
        string temporary = Path.Combine(DirectoryPath, $"image-{id}.tmp");
        Directory.CreateDirectory(storage);
        using var session = new Session(new SessionSettings(sessionName, storage)
        {
            CpuCount = 1,
            MemorySizeInMB = 512
        });
        bool started = false;
        bool terminated = false;
        try
        {
            started = true;
            session.Start();
            await WslFixtureRunner.PullImageAsync(
                session, WslProtectedEvaluationRunner.EvaluationImage, cancellationToken);
            var tag = await RunDaemonCommandAsync(sessionName,
                ["docker", "tag", WslProtectedEvaluationRunner.EvaluationImage, Name], TimeSpan.FromSeconds(20));
            if (tag.ExitCode != 0)
            {
                throw new IOException($"The pinned image could not be tagged: {tag.StandardError}");
            }
            await ExportAsync(sessionName, temporary, cancellationToken);
            string hash = Validate(temporary);
            File.Move(temporary, ArchivePath);
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "image.json"), JsonSerializer.Serialize(new
            {
                image = Name,
                originalReference = WslProtectedEvaluationRunner.EvaluationImage,
                configSha256 = ConfigSha256,
                archiveSha256 = hash,
                acquisition = "Host-side native WSLC pull and debug-only docker save; not an Evaluation fallback",
                preparedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            return hash;
        }
        finally
        {
            if (started)
            {
                session.Terminate();
                terminated = true;
            }
            if (!started || terminated)
            {
                Directory.Delete(storage, recursive: true);
            }
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string Validate(string path)
    {
        using var archive = File.OpenRead(path);
        using var reader = new TarReader(archive);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonDocument? manifest = null;
        JsonDocument? config = null;
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                if (entry.DataStream is null) continue;
                if (!hashes.TryAdd(entry.Name, string.Empty))
                {
                    throw new IOException("The image archive contains duplicate files.");
                }
                if (entry.Name == "manifest.json")
                {
                    manifest = JsonDocument.Parse(ReadSmall(entry.DataStream));
                }
                else if (entry.Name.EndsWith(ConfigSha256, StringComparison.Ordinal)
                    || entry.Name == $"{ConfigSha256}.json")
                {
                    byte[] bytes = ReadSmall(entry.DataStream);
                    hashes[entry.Name] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    config = JsonDocument.Parse(bytes);
                }
                else
                {
                    hashes[entry.Name] = Convert.ToHexStringLower(SHA256.HashData(entry.DataStream));
                }
            }
            if (manifest is null || config is null || manifest.RootElement.GetArrayLength() != 1)
            {
                throw new IOException("The archive is not the single pinned Docker-save image.");
            }
            var image = manifest.RootElement[0];
            string configPath = image.GetProperty("Config").GetString()
                ?? throw new IOException("The image configuration path is missing.");
            if (!hashes.TryGetValue(configPath, out string? configHash) || configHash != ConfigSha256
                || !image.GetProperty("RepoTags").EnumerateArray().Any(tag => tag.GetString() == Name)
                || config.RootElement.GetProperty("os").GetString() != "linux"
                || config.RootElement.GetProperty("architecture").GetString() != "amd64")
            {
                throw new IOException("The image identity, tag, or architecture does not match the pinned baseline.");
            }
            string[] layers = image.GetProperty("Layers").EnumerateArray()
                .Select(layer => layer.GetString() ?? "").ToArray();
            string[] diffIds = config.RootElement.GetProperty("rootfs").GetProperty("diff_ids").EnumerateArray()
                .Select(layer => layer.GetString() ?? "").ToArray();
            if (layers.Length != diffIds.Length
                || layers.Where((layer, i) => !hashes.TryGetValue(layer, out string? hash)
                    || $"sha256:{hash}" != diffIds[i]).Any())
            {
                throw new IOException("The image layers do not match the pinned configuration.");
            }
        }
        finally
        {
            manifest?.Dispose();
            config?.Dispose();
        }
        archive.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(archive));
    }

    private static byte[] ReadSmall(Stream stream)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (bytes.Length + count > 1024 * 1024)
            {
                throw new IOException("The image manifest or configuration exceeds its limit.");
            }
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static async Task ExportAsync(string sessionName, string path, CancellationToken cancellationToken)
    {
        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "--session", sessionName, "system", "session", "run", "docker", "save", Name
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("The image export did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync();
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            var buffer = new byte[65536];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token)) != 0)
            {
                if (file.Length + count > 256 * 1024 * 1024)
                {
                    throw new IOException("The pinned image export exceeds its size limit.");
                }
                await file.WriteAsync(buffer.AsMemory(0, count), linked.Token);
            }
            await process.WaitForExitAsync(linked.Token);
            if (process.ExitCode != 0)
            {
                throw new IOException($"The pinned image export failed: {await error}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}

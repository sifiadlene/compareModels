using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace IsolatedCopilotRun;

public sealed record PreparedImage(string Tag, string ImageId, string TarPath, string TarSha256);

/// <summary>Builds the agent and Task images with <c>wslc build</c> and exports them as tars.</summary>
public static class RunImages
{
    public const string CliVersion = "1.0.112";
    public const string CliTarballSha256 = "dfe7e59acf04f0b7f59b024ea9db101a9246503e9bf7152eb668fbb2a469b151";
    private const string CliUrl =
        $"https://github.com/github/copilot-cli/releases/download/v{CliVersion}/copilot-linux-x64.tar.gz";

    public static string WslcPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "compareModels", "IsolatedRun", "cache");

    public static async Task<string> StageCliAsync(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(CacheDirectory, $"copilot-{CliVersion}");
        string binary = Path.Combine(directory, "copilot");
        string tarball = Path.Combine(directory, "copilot-linux-x64.tar.gz");
        Directory.CreateDirectory(directory);
        if (!File.Exists(tarball) || await HashFileAsync(tarball, cancellationToken) != CliTarballSha256)
        {
            using var http = new HttpClient();
            await using (var stream = await http.GetStreamAsync(CliUrl, cancellationToken))
            await using (var file = File.Create(tarball))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            string actual = await HashFileAsync(tarball, cancellationToken);
            if (actual != CliTarballSha256)
            {
                File.Delete(tarball);
                throw new InvalidOperationException($"Copilot CLI tarball SHA-256 mismatch: {actual}");
            }
        }

        File.Delete(binary);
        await using (var compressed = File.OpenRead(tarball))
        await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    && entry.Name.TrimStart('.', '/') == "copilot")
                {
                    await entry.ExtractToFileAsync(binary, overwrite: true, cancellationToken);
                }
            }
        }

        return File.Exists(binary) ? binary : throw new InvalidOperationException("The CLI tarball has no copilot binary.");
    }

    public static async Task<(PreparedImage Agent, PreparedImage Task)> PrepareForTaskAsync(
        TaskDefinition task, Action<string> log, CancellationToken cancellationToken)
    {
        string images = Path.Combine(AppContext.BaseDirectory, "images");
        string cli = await StageCliAsync(cancellationToken);
        var agent = await PrepareAsync("agent.Dockerfile",
            [cli, .. new[] { "mailbox-mcp", "start-cli", "drop-workload", "check-state" }.Select(file => Path.Combine(images, file))],
            "", "comparemodels-agent", log, cancellationToken);
        var taskImage = await PrepareAsync("executor.Dockerfile",
            [Path.Combine(images, "drop-workload"), Path.Combine(images, "check-state")],
            task.Packages, $"comparemodels-task-{task.Id}", log, cancellationToken);
        return (agent, taskImage);
    }

    public static async Task<PreparedImage> PrepareAsync(
        string dockerfile,
        IReadOnlyList<string> files,
        string packages,
        string repository,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string imagesDirectory = Path.Combine(AppContext.BaseDirectory, "images");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes($"packages={packages}\n"));
        string context = Directory.CreateTempSubdirectory("compareModels-build-").FullName;
        try
        {
            foreach (string file in files.Append(Path.Combine(imagesDirectory, dockerfile)))
            {
                string target = Path.Combine(context, Path.GetFileName(file));
                File.Copy(file, target);
                digest.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n"));
                await using var stream = File.OpenRead(target);
                digest.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            string tag = $"{repository}:{Convert.ToHexStringLower(digest.GetHashAndReset())[..16]}";
            string tarPath = Path.Combine(CacheDirectory, "images", tag.Replace(':', '-') + ".tar");
            string idPath = tarPath + ".id";
            if (File.Exists(tarPath) && File.Exists(idPath))
            {
                log($"Reusing cached image {tag}.");
                return new(tag, (await File.ReadAllTextAsync(idPath, cancellationToken)).Trim(), tarPath,
                    await HashFileAsync(tarPath, cancellationToken));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
            string iidFile = Path.Combine(context, "iid");
            log($"Building {tag} with wslc build...");
            await WslcAsync(cancellationToken, "build", "--progress", "plain", "-f",
                Path.Combine(context, dockerfile), "-t", tag, "--iidfile", iidFile,
                "--build-arg", $"TASK_PACKAGES={packages}", context);
            string imageId = (await File.ReadAllTextAsync(iidFile, cancellationToken)).Trim();
            string partial = tarPath + ".partial";
            await WslcAsync(cancellationToken, "save", "-o", partial, tag);
            File.Move(partial, tarPath, overwrite: true);
            await File.WriteAllTextAsync(idPath, imageId, cancellationToken);
            return new(tag, imageId, tarPath, await HashFileAsync(tarPath, cancellationToken));
        }
        finally
        {
            Directory.Delete(context, recursive: true);
        }
    }

    public static async Task<string> WslcAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(WslcPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("wslc did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string text = await output + await error;
        if (process.ExitCode != 0)
        {
            string tail = text.Length > 4000 ? text[^4000..] : text;
            throw new InvalidOperationException($"wslc {arguments[0]} failed ({process.ExitCode}): {tail}");
        }

        return text;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

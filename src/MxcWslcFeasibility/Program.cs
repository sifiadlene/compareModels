using Microsoft.Mxc.Sdk.V1;
using MxcWslcFeasibility;
using System.Text.Json;
using System.Text.Json.Serialization;
using WslContainerFeasibility;

if (args is [var sampleName, "--accept-required-telemetry"]
    && Enum.TryParse<EvaluationSample>(sampleName, ignoreCase: true, out var sample)
    && Enum.IsDefined(sample))
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var result = await new MxcEvaluationRunner().RunAsync(sample, true, cancellation.Token);
        Console.WriteLine($"{result.Outcome}: {result.Summary}");
        Console.WriteLine($"Evidence: {result.EvidencePath}");
        Console.WriteLine($"Retained project: {result.ProjectDirectoryPath ?? "unavailable"}");
        return result.Outcome == EvaluationOutcome.Passed ? 0 : 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

if (args is ["prepare-image"])
{
    Console.WriteLine($"Validated image SHA256: {await MxcImage.PrepareAsync(CancellationToken.None)}");
    Console.WriteLine($"Image archive: {MxcImage.ArchivePath}");
    return 0;
}

if (args is not ["identify", "--accept-required-telemetry"])
{
    Console.Error.WriteLine("Usage: prepare-image | <correct|incorrect|tampered|identify> --accept-required-telemetry");
    return 2;
}

Console.WriteLine($"Native MXC: {MxcPlatform.NativeVersion}");
Console.WriteLine($"MXC consent state (does not disable Required events): {MxcTelemetry.GetConsent()}");
Console.WriteLine($"Optional telemetry policy: {MxcTelemetry.GetPolicy()}");
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
Console.WriteLine(JsonSerializer.Serialize(MxcPlatform.GetPlatformSupport(), options));
Console.WriteLine(JsonSerializer.Serialize(MxcPlatform.GetAvailableBackends(), options));
return 0;

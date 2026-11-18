using IsolatedCopilotRun;

string command = args.Length > 0 ? args[0] : "";
string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string taskDirectory = Option("--task") ?? Path.Combine(AppContext.BaseDirectory, "tasks", "slugify");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

switch (command)
{
    case "gate":
        return await IsolatedRun.GateAsync(taskDirectory, cancellation.Token);
    case "run" when Option("--model") is { } model:
        return await IsolatedRun.RunAsync(taskDirectory, model, cancellation.Token);
    default:
        Console.Error.WriteLine("Usage: IsolatedCopilotRun gate [--task <dir>]");
        Console.Error.WriteLine("       IsolatedCopilotRun run --model <id> [--task <dir>]");
        Console.Error.WriteLine("run requires COPILOT_GITHUB_TOKEN and COMPAREMODELS_LIVE_RUN=1.");
        return 2;
}

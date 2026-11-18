using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// MCP stdio server exposing one run_command tool. It never executes commands:
// requests are written to the mailbox and the host relays them to the executor.
string requests = args.Length > 0 ? args[0] : "/mailbox/requests";
string responses = args.Length > 1 ? args[1] : "/mailbox/responses";
var stdout = Console.OpenStandardOutput();

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    JsonNode? message;
    try
    {
        message = JsonNode.Parse(line);
    }
    catch (System.Text.Json.JsonException)
    {
        continue;
    }

    if (message is not JsonObject request || request["id"] is not JsonNode id)
    {
        continue;
    }

    string method = request["method"]?.GetValue<string>() ?? "";
    JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
    switch (method)
    {
        case "initialize":
            reply["result"] = new JsonObject
            {
                ["protocolVersion"] = request["params"]?["protocolVersion"]?.DeepClone() ?? "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "sandbox", ["version"] = "1.0.0" }
            };
            break;
        case "ping":
            reply["result"] = new JsonObject();
            break;
        case "tools/list":
            reply["result"] = new JsonObject { ["tools"] = new JsonArray(ToolDefinition()) };
            break;
        case "tools/call":
            reply["result"] = await CallAsync(request["params"] as JsonObject);
            break;
        default:
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            break;
    }

    byte[] bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
    await stdout.WriteAsync(bytes);
    await stdout.FlushAsync();
}

return 0;

static JsonObject ToolDefinition() => new()
{
    ["name"] = "run_command",
    ["description"] =
        "Run a /bin/sh command in the Task's sandboxed executor container. The working directory is " +
        "/workspace, which contains the Task files. Returns the exit code and combined output.",
    ["inputSchema"] = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "Shell command to run." },
            ["timeoutSeconds"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 600,
                ["description"] = "Optional timeout in seconds (default 120)."
            }
        },
        ["required"] = new JsonArray("command"),
        ["additionalProperties"] = false
    }
};

async Task<JsonObject> CallAsync(JsonObject? parameters)
{
    if (parameters?["name"]?.GetValue<string>() != "run_command"
        || parameters["arguments"] is not JsonObject arguments
        || arguments["command"] is not JsonValue commandValue
        || !commandValue.TryGetValue(out string? command)
        || string.IsNullOrEmpty(command))
    {
        return Result("run_command requires a non-empty string 'command'.", isError: true);
    }

    int timeout = 120;
    if (arguments["timeoutSeconds"] is JsonValue timeoutValue
        && (!timeoutValue.TryGetValue(out timeout) || timeout is < 1 or > 600))
    {
        return Result("timeoutSeconds must be an integer between 1 and 600.", isError: true);
    }

    string requestId = Guid.NewGuid().ToString("N");
    byte[] body = Encoding.UTF8.GetBytes(new JsonObject
    {
        ["id"] = requestId,
        ["command"] = command,
        ["timeoutSeconds"] = timeout
    }.ToJsonString());
    string hash = Convert.ToHexStringLower(SHA256.HashData(body));
    string temporary = Path.Combine(requests, $".{requestId}.tmp");
    await File.WriteAllBytesAsync(temporary, body);
    File.Move(temporary, Path.Combine(requests, $"{requestId}.json"));

    string responsePath = Path.Combine(responses, $"{requestId}.json");
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeout + 60);
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(responsePath))
        {
            JsonObject? response;
            try
            {
                response = JsonNode.Parse(await File.ReadAllTextAsync(responsePath)) as JsonObject;
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                await Task.Delay(200);
                continue;
            }

            if (response?["id"]?.GetValue<string>() != requestId
                || response["requestSha256"]?.GetValue<string>() != hash)
            {
                return Result("Mailbox integrity check failed; the command result was discarded.", isError: true);
            }

            string status = response["status"]?.GetValue<string>() ?? "unknown";
            string output = response["output"]?.GetValue<string>() ?? "";
            if (status != "completed")
            {
                return Result($"Command {status}: {response["reason"]?.GetValue<string>() ?? ""}\n{output}", isError: true);
            }

            int exitCode = response["exitCode"]?.GetValue<int>() ?? -1;
            return Result($"exit code: {exitCode}\n{output}", isError: exitCode != 0);
        }

        await Task.Delay(200);
    }

    return Result("Timed out waiting for the host to relay the command.", isError: true);
}

static JsonObject Result(string text, bool isError) => new()
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    ["isError"] = isError
};

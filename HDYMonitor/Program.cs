using HDYMonitor.Services;

Console.WriteLine("HDYMonitor console starting.");

var intervalSeconds = GetIntervalSeconds(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (intervalSeconds <= 0)
{
    var result = await RunOnceAsync(cts.Token);
    Environment.ExitCode = result.Success ? 0 : 1;
    return;
}

Console.WriteLine($"Running every {intervalSeconds} seconds. Press Ctrl+C to stop.");

while (!cts.IsCancellationRequested)
{
    var result = await RunOnceAsync(cts.Token);
    if (!result.Success)
    {
        Console.WriteLine($"Run failed. StatusCode={result.StatusCode}. Message={result.Message}");
        if (result.Solutions is { Length: > 0 })
        {
            Console.WriteLine("Solutions:");
            foreach (var solution in result.Solutions)
            {
                Console.WriteLine(solution);
            }
        }
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
    }
    catch (TaskCanceledException)
    {
        break;
    }
}

static int GetIntervalSeconds(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var seconds))
            {
                return seconds;
            }
        }

        if (args[i].StartsWith("--interval=", StringComparison.OrdinalIgnoreCase))
        {
            var value = args[i].Substring("--interval=".Length);
            if (int.TryParse(value, out var seconds))
            {
                return seconds;
            }
        }
    }

    var envValue = Environment.GetEnvironmentVariable("RUN_INTERVAL_SECONDS");
    return int.TryParse(envValue, out var envSeconds) ? envSeconds : 0;
}

static async Task<FetchActivityResult> RunOnceAsync(CancellationToken cancellationToken)
{
    Console.WriteLine($"Run started at {DateTimeOffset.Now:O}");
    var result = await FetchActivityService.FetchAndProcessActivityAsync(cancellationToken);
    Console.WriteLine($"Run finished. StatusCode={result.StatusCode}. Message={result.Message}");
    return result;
}

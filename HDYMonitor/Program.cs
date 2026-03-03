using HDYMonitor.Services;

using Aliyun.Base.Utils;

Console.WriteLine("HDYMonitor console starting.");

var intervalSeconds = GetIntervalSeconds(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await SendStartupPushDeerTestAsync();
var startupConfigResult = await RunStartupConfigCheckAsync(cts.Token);
var skipFirstScheduledConfigCheck = startupConfigResult.Success;

if (intervalSeconds <= 0)
{
    var result = await RunOnceAsync(cts.Token, includeConfigCheck: !skipFirstScheduledConfigCheck);
    Environment.ExitCode = result.Success ? 0 : 1;
    return;
}

Console.WriteLine($"Running every {intervalSeconds} seconds. Press Ctrl+C to stop.");

var isFirstRun = true;
while (!cts.IsCancellationRequested)
{
    var includeConfigCheck = !(skipFirstScheduledConfigCheck && isFirstRun);
    var result = await RunOnceAsync(cts.Token, includeConfigCheck);
    isFirstRun = false;

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

static async Task<FetchActivityResult> RunOnceAsync(CancellationToken cancellationToken, bool includeConfigCheck = true)
{
    Console.WriteLine($"Run started at {DateTimeOffset.Now:O}");
    var activityResult = await FetchActivityService.FetchAndProcessActivityAsync(cancellationToken);
    FetchActivityResult configResult;
    if (includeConfigCheck)
    {
        configResult = await FetchConfigIdService.CheckAndNotifyAsync(cancellationToken);
        if (!configResult.Success)
        {
            Console.WriteLine($"Config ID check failed. StatusCode={configResult.StatusCode}. Message={configResult.Message}");
        }
    }
    else
    {
        configResult = new FetchActivityResult(true, 200, "Config ID check skipped (already executed at startup).");
    }

    var success = activityResult.Success && configResult.Success;
    var statusCode = activityResult.Success ? configResult.StatusCode : activityResult.StatusCode;
    var message = $"{activityResult.Message}; {configResult.Message}";
    var result = new FetchActivityResult(success, statusCode, message);

    Console.WriteLine($"Run finished. StatusCode={result.StatusCode}. Message={result.Message}");
    return result;
}

static async Task<FetchActivityResult> RunStartupConfigCheckAsync(CancellationToken cancellationToken)
{
    Console.WriteLine("Running startup config ID check...");
    var configResult = await FetchConfigIdService.CheckAndNotifyAsync(cancellationToken);
    if (!configResult.Success)
    {
        Console.WriteLine($"Startup config ID check failed. StatusCode={configResult.StatusCode}. Message={configResult.Message}");
    }

    Console.WriteLine($"Startup config ID check finished. StatusCode={configResult.StatusCode}. Message={configResult.Message}");
    return configResult;
}

static async Task SendStartupPushDeerTestAsync()
{
    Console.WriteLine("Sending startup PushDeer test message...");

    var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
    var testTitle = "HDYMonitor 启动测试";
    var testMessage = $"程序已启动，时间：{now}";

    await SendHelper.SendPushDeer(testTitle, testMessage);
}

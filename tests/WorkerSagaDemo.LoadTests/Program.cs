using NBomber.Contracts.Stats;
using NBomber.CSharp;
using WorkerSagaDemo.LoadTests.Scenarios;

// Default base URL points at the Aspire-pinned API port.
// Override with --base-url arg or LOAD_TEST_BASE_URL env var.
var baseUrl = args
    .SkipWhile(a => a != "--base-url")
    .Skip(1)
    .FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("LOAD_TEST_BASE_URL")
    ?? "http://localhost:5041";

// Pick scenario from first positional arg (default: outbox)
var scenarioName = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "outbox";

Console.WriteLine($"Load test target: {baseUrl}");
Console.WriteLine($"Scenario: {scenarioName}");
Console.WriteLine();

switch (scenarioName.ToLowerInvariant())
{
    case "outbox":
        NBomberRunner
            .RegisterScenarios(OutboxThroughputScenario.Create(baseUrl))
            .WithReportFolder("reports")
            .WithReportFileName("outbox_throughput")
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
            .Run();
        break;

    case "mixed":
        NBomberRunner
            .RegisterScenarios(MixedWorkloadScenario.Create(baseUrl))
            .WithReportFolder("reports")
            .WithReportFileName("mixed_workload")
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
            .Run();
        break;

    default:
        Console.WriteLine($"Unknown scenario: {scenarioName}");
        Console.WriteLine("Available: outbox, mixed");
        Environment.Exit(1);
        break;
}

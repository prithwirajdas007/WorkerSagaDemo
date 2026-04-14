using System.Diagnostics;

namespace WorkerSagaDemo.Worker;

/// <summary>
/// Owns the ActivitySource used by the saga for custom OpenTelemetry spans.
///
/// The source name is what OpenTelemetry calls a "tracer name" -- any code that
/// wants to emit spans under this name uses SagaTelemetry.ActivitySource.StartActivity().
/// The OTel SDK has to be told about this name via AddSource("WorkerSagaDemo.Saga"),
/// otherwise the spans are dropped silently. See Program.cs for that registration.
///
/// Convention: one ActivitySource per logical component. We could split this into
/// multiple sources (one per saga class) if the project grew, but for one saga it's
/// cleaner to keep them together.
/// </summary>
public static class SagaTelemetry
{
    public const string ActivitySourceName = "WorkerSagaDemo.Saga";

    /// <summary>
    /// The ActivitySource used by the saga. Call StartActivity() on this to emit
    /// a span. If no listener is subscribed (e.g. during unit tests without OTel
    /// configured), StartActivity returns null -- the saga code handles this
    /// gracefully by using null-conditional operators when tagging.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // Tag name constants. Using constants keeps tag names consistent across
    // handlers and makes it easy to find all the places that reference a tag.
    // OTel convention for custom tags: dot-separated lowercase, scoped by component.
    public const string TagJobId = "saga.job_id";
    public const string TagStepIndex = "saga.step_index";
    public const string TagStepName = "saga.step_name";
    public const string TagStepCount = "saga.total_steps";
    public const string TagCompletedSteps = "saga.completed_steps";
    public const string TagOutcome = "saga.outcome";
}

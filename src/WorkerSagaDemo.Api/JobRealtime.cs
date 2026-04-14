using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using WorkerSagaDemo.Contracts.Contracts;

namespace WorkerSagaDemo.Api;

// =============================================================================
// JobHub
// SignalR hub that browser clients connect to for real-time job updates.
// Clients join by calling /hubs/jobs. They receive "JobStatusChanged" messages
// pushed from the JobStatusChangedHandler below.
// =============================================================================

public class JobHub : Hub
{
    // No server-side methods needed for this demo -- clients only receive
    // pushes from the server. If we wanted client-callable methods later
    // (e.g. "subscribe only to this JobId"), we'd add them here.
}

// =============================================================================
// JobStatusChangedHandler
// Rebus handler that receives JobStatusChanged events published by the
// Worker's saga, and forwards them to all connected SignalR clients.
//
// This handler lives in the API (not the Worker) because SignalR connections
// are held by the web server. The API subscribes to the event via Rebus
// Pub/Sub on startup.
// =============================================================================

public class JobStatusChangedHandler : IHandleMessages<JobStatusChanged>
{
    private readonly IHubContext<JobHub> _hub;
    private readonly ILogger<JobStatusChangedHandler> _logger;

    public JobStatusChangedHandler(
        IHubContext<JobHub> hub,
        ILogger<JobStatusChangedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(JobStatusChanged message)
    {
        _logger.LogInformation(
            "Pushing JobStatusChanged for {JobId} -> {Status} to SignalR clients",
            message.JobId, message.Status);

        // "JobStatusChanged" is the client-side method name -- browser JS
        // subscribes to this event by the same string.
        await _hub.Clients.All.SendAsync("JobStatusChanged", message);
    }
}

using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using WorkerSagaDemo.Contracts;
using WorkerSagaDemo.Contracts.Contracts;

namespace WorkerSagaDemo.Worker.Handlers
{
    public class PingMessageHandler : IHandleMessages<PingMessage>
    {
        private readonly ILogger<PingMessageHandler> _logger;

        public PingMessageHandler(ILogger<PingMessageHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(PingMessage message)
        {
            _logger.LogInformation(
                "Got PingMessage {Id} created at {CreatedAt}",
                message.Id,
                message.CreatedAt);

            return Task.CompletedTask;
        }
    }
}

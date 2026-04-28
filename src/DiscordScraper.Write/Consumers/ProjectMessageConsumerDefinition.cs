using MassTransit;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Two immediate retries handle transient Mongo blips; the saga handles ProjectMessage.Faulted
/// for non-transient failures.
/// </summary>
internal sealed class ProjectMessageConsumerDefinition : ConsumerDefinition<ProjectMessageConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ProjectMessageConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(2));
    }
}

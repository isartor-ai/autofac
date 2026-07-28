using Agentwerke.AgentSecOps;
using Agentwerke.Application.Agents;
using Agentwerke.Application.Observability;
using Agentwerke.Application.Secrets;
using Agentwerke.Application.Workflows;
using Agentwerke.Infrastructure.Persistence;
using Agentwerke.Infrastructure.Policies;
using Agentwerke.Infrastructure.Secrets;
using Agentwerke.Infrastructure.Workers;
using Agentwerke.Infrastructure.Workflows;
using Agentwerke.Workflows.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agentwerke.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentwerkeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
        }

        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
            .EnableDynamicJson()
            .Build();

        services.AddDbContext<AgentwerkeDbContext>(options =>
            options.UseNpgsql(dataSource));

        var runtimeOptions = WorkflowRuntimeOptions.Resolve(configuration);
        services.AddSingleton(runtimeOptions);
        services.AddHostedService<WorkflowRuntimeStartupLogger>();

        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddScoped<IWorkflowValidationService, WorkflowValidationService>();
        services.AddScoped<IWorkflowAuthoringService, WorkflowAuthoringService>();
        services.AddScoped<ITemplateCatalogService, TemplateCatalogService>();
        services.AddScoped<IWorkflowRuntimeStore, WorkflowRuntimeStore>();
        services.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
        services.AddScoped<IRunContextRepository, RunContextRepository>();
        services.AddScoped<IExternalWorkflowEventRepository, ExternalWorkflowEventRepository>();
        services.AddScoped<IWaitingExternalCorrelationRepository, WaitingExternalCorrelationRepository>();
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<Application.Agents.IAgentInteractionRepository, AgentInteractionRepository>();
        services.AddScoped<Application.Agents.IInteractionDeliveryRepository, InteractionDeliveryRepository>();
        services.AddScoped<IWorkflowRunner, WorkflowRunnerAdapter>();
        services.AddSingleton<Application.Agents.IAgentFeedbackStore, Application.Agents.InMemoryAgentFeedbackStore>();
        services.AddScoped<IWorkflowRunOrchestrationService, WorkflowRunOrchestrationService>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IAuditQuery, AuditRepository>();
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();

        // Camunda services are only wired when the runtime is explicitly opted into
        // Camunda mode. The default Agentwerke runtime keeps those dependencies inactive.
        if (runtimeOptions.Mode == WorkflowRuntimeMode.Camunda)
        {
            services.Configure<CamundaOptions>(o =>
                configuration.GetSection(CamundaOptions.Section).Bind(o));
            services.AddHttpClient<CamundaClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<CamundaOptions>>().Value;
                if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
                {
                    client.BaseAddress = baseUri;
                }

                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 10);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
            services.AddScoped<ICamundaClient>(sp => sp.GetRequiredService<CamundaClient>());
            services.AddScoped<ICamundaRuntimeStatusService, CamundaRuntimeStatusService>();
        }
        else
        {
            services.AddSingleton<ICamundaRuntimeStatusService, DisabledCamundaRuntimeStatusService>();
        }

        services.Configure<PolicyStoreOptions>(configuration.GetSection(PolicyStoreOptions.SectionName));
        services.AddSingleton<IPolicyRuleStore, FilePolicyRuleStore>();

        services.AddScoped<IRunOutbox, OutboxRepository>();
        services.AddScoped<IWorkflowRunExecutor, WorkflowRunExecutor>();
        services.Configure<InteractionOptions>(configuration.GetSection(InteractionOptions.Section));

        // Agentwerke.Application deliberately depends on nothing but Domain and logging — it has no
        // Microsoft.Extensions.Options reference — so the router and resolver take a plain
        // InteractionOptions. Project the bound IOptions value so both styles share one config source.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<InteractionOptions>>().Value);
        services.AddScoped<IInteractionChannelResolver, InteractionChannelResolver>();
        services.AddScoped<IInteractionRouter, InteractionRouter>();

        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddHostedService<RunDispatchWorker>();
        services.AddHostedService<InteractionTimeoutSweeper>();

        services.Configure<WaitingExternalMonitorOptions>(
            configuration.GetSection(WaitingExternalMonitorOptions.Section));
        services.AddHostedService<WaitingExternalMonitor>();

        // Default no-op notifier; AddAgentwerkeIntegrations overrides it with the
        // connector-backed implementation when chat channels are configured (#31).
        services.TryAddScoped<Application.Notifications.IApprovalNotifier, Application.Notifications.NullApprovalNotifier>();

        return services;
    }
}

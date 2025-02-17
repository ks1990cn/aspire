// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Utils;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

internal class AppResource
{
    public IResource ModelResource { get; }
    public CustomResource DcpResource { get; }
    public virtual List<ServiceAppResource> ServicesProduced { get; } = [];
    public virtual List<ServiceAppResource> ServicesConsumed { get; } = [];

    public AppResource(IResource modelResource, CustomResource dcpResource)
    {
        ModelResource = modelResource;
        DcpResource = dcpResource;
    }
}

internal sealed class ServiceAppResource : AppResource
{
    public Service Service => (Service)DcpResource;
    public EndpointAnnotation EndpointAnnotation { get; }
    public ServiceProducerAnnotation DcpServiceProducerAnnotation { get; }

    public override List<ServiceAppResource> ServicesProduced
    {
        get { throw new InvalidOperationException("Service resources do not produce any services"); }
    }
    public override List<ServiceAppResource> ServicesConsumed
    {
        get { throw new InvalidOperationException("Service resources do not consume any services"); }
    }

    public ServiceAppResource(IResource modelResource, Service service, EndpointAnnotation sba) : base(modelResource, service)
    {
        EndpointAnnotation = sba;
        DcpServiceProducerAnnotation = new(service.Metadata.Name);
    }
}

internal sealed class ApplicationExecutor(ILogger<ApplicationExecutor> logger,
                                          ILogger<DistributedApplication> distributedApplicationLogger,
                                          DistributedApplicationModel model,
                                          DistributedApplicationOptions distributedApplicationOptions,
                                          IKubernetesService kubernetesService,
                                          IEnumerable<IDistributedApplicationLifecycleHook> lifecycleHooks,
                                          IConfiguration configuration,
                                          IOptions<DcpOptions> options,
                                          IDashboardEndpointProvider dashboardEndpointProvider,
                                          DistributedApplicationExecutionContext executionContext,
                                          ResourceNotificationService notificationService,
                                          ResourceLoggerService loggerService)
{
    private const string DebugSessionPortVar = "DEBUG_SESSION_PORT";

    private readonly ILogger<ApplicationExecutor> _logger = logger;
    private readonly DistributedApplicationModel _model = model;
    private readonly IDistributedApplicationLifecycleHook[] _lifecycleHooks = lifecycleHooks.ToArray();
    private readonly IOptions<DcpOptions> _options = options;
    private readonly IDashboardEndpointProvider _dashboardEndpointProvider = dashboardEndpointProvider;
    private readonly DistributedApplicationExecutionContext _executionContext = executionContext;
    private readonly List<AppResource> _appResources = [];

    public async Task RunApplicationAsync(CancellationToken cancellationToken = default)
    {
        AspireEventSource.Instance.DcpModelCreationStart();
        try
        {
            if (!distributedApplicationOptions.DisableDashboard)
            {
                if (_model.Resources.SingleOrDefault(r => StringComparers.ResourceName.Equals(r.Name, KnownResourceNames.AspireDashboard)) is not { } dashboardResource)
                {
                    // No dashboard is specified, so start one.
                    // TODO validate that the dashboard has not been suppressed
                    await StartDashboardAsDcpExecutableAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ConfigureAspireDashboardResource(dashboardResource, cancellationToken).ConfigureAwait(false);
                }
            }

            PrepareServices();
            PrepareContainers();
            PrepareExecutables();

            await CreateServicesAsync(cancellationToken).ConfigureAwait(false);

            await CreateContainersAndExecutablesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var lifecycleHook in _lifecycleHooks)
            {
                await lifecycleHook.AfterResourcesCreatedAsync(_model, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            AspireEventSource.Instance.DcpModelCreationStop();
        }
    }

    private async Task ConfigureAspireDashboardResource(IResource dashboardResource, CancellationToken cancellationToken)
    {
        // Don't publish the resource to the manifest.
        dashboardResource.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

        // Remove endpoint annotations because we are directly configuring
        // the dashboard app (it doesn't go through the proxy!).
        var endpointAnnotations = dashboardResource.Annotations.OfType<EndpointAnnotation>().ToList();
        foreach (var endpointAnnotation in endpointAnnotations)
        {
            dashboardResource.Annotations.Remove(endpointAnnotation);
        }

        // Get resource endpoint URL.
        var grpcEndpointUrl = await _dashboardEndpointProvider.GetResourceServiceUriAsync(cancellationToken).ConfigureAwait(false);

        dashboardResource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            if (configuration["ASPNETCORE_URLS"] is not { } appHostApplicationUrl)
            {
                throw new DistributedApplicationException("Failed to configure dashboard resource because ASPNETCORE_URLS environment variable was not set.");
            }

            if (configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] is not { } otlpEndpointUrl)
            {
                throw new DistributedApplicationException("Failed to configure dashboard resource because DOTNET_DASHBOARD_OTLP_ENDPOINT_URL environment variable was not set.");
            }

            // Grab the resource service URL. We need to inject this into the resource.

            context.EnvironmentVariables["ASPNETCORE_URLS"] = appHostApplicationUrl;
            context.EnvironmentVariables["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = grpcEndpointUrl;
            context.EnvironmentVariables["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = otlpEndpointUrl;
        }));
    }

    private async Task StartDashboardAsDcpExecutableAsync(CancellationToken cancellationToken = default)
    {
        if (!distributedApplicationOptions.DashboardEnabled)
        {
            // The dashboard is disabled. Do nothing.
            return;
        }

        if (_options.Value.DashboardPath is not { } dashboardPath)
        {
            throw new DistributedApplicationException("Dashboard path empty or file does not exist.");
        }

        var fullyQualifiedDashboardPath = Path.GetFullPath(dashboardPath);
        var dashboardWorkingDirectory = Path.GetDirectoryName(fullyQualifiedDashboardPath);

        var dashboardExecutableSpec = new ExecutableSpec
        {
            ExecutionType = ExecutionType.Process,
            WorkingDirectory = dashboardWorkingDirectory
        };

        if (string.Equals(".dll", Path.GetExtension(fullyQualifiedDashboardPath), StringComparison.OrdinalIgnoreCase))
        {
            // The dashboard path is a DLL, so run it with `dotnet <dll>`
            dashboardExecutableSpec.ExecutablePath = "dotnet";
            dashboardExecutableSpec.Args = [fullyQualifiedDashboardPath];
        }
        else
        {
            // Assume the dashboard path is directly executable
            dashboardExecutableSpec.ExecutablePath = fullyQualifiedDashboardPath;
        }

        var grpcEndpointUrl = await _dashboardEndpointProvider.GetResourceServiceUriAsync(cancellationToken).ConfigureAwait(false);

        // Matches DashboardWebApplication.DashboardUrlDefaultValue
        const string defaultDashboardUrl = "http://localhost:18888";

        var otlpEndpointUrl = configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"];
        var dashboardUrls = configuration["ASPNETCORE_URLS"] ?? defaultDashboardUrl;
        var aspnetcoreEnvironment = configuration["ASPNETCORE_ENVIRONMENT"];

        dashboardExecutableSpec.Env =
        [
            new()
            {
                Name = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL",
                Value = grpcEndpointUrl
            },
            new()
            {
                Name = "ASPNETCORE_URLS",
                Value = dashboardUrls
            },
            new()
            {
                Name = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
                Value = otlpEndpointUrl
            },
            new()
            {
                Name = "ASPNETCORE_ENVIRONMENT",
                Value = aspnetcoreEnvironment
            }
        ];

        var dashboardExecutable = new Executable(dashboardExecutableSpec)
        {
            Metadata = { Name = KnownResourceNames.AspireDashboard }
        };

        await kubernetesService.CreateAsync(dashboardExecutable, cancellationToken).ConfigureAwait(false);
        PrintDashboardUrls(dashboardUrls);
    }

    private void PrintDashboardUrls(string delimitedUrlList)
    {
        if (StringUtils.TryGetUriFromDelimitedString(delimitedUrlList, ";", out var firstDashboardUrl))
        {
            distributedApplicationLogger.LogInformation("Now listening on: {DashboardUrl}", firstDashboardUrl.ToString().TrimEnd('/'));
        }
    }

    private async Task CreateServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AspireEventSource.Instance.DcpServicesCreationStart();

            var needAddressAllocated = _appResources.OfType<ServiceAppResource>().Where(sr => !sr.Service.HasCompleteAddress).ToList();

            await CreateResourcesAsync<Service>(cancellationToken).ConfigureAwait(false);

            if (needAddressAllocated.Count == 0)
            {
                // No need to wait for any updates to Service objects from the orchestrator.
                return;
            }

            // We do not specify the initial list version, so the watcher will give us all updates to Service objects.
            IAsyncEnumerable<(WatchEventType, Service)> serviceChangeEnumerator = kubernetesService.WatchAsync<Service>(cancellationToken: cancellationToken);
            await foreach (var (evt, updated) in serviceChangeEnumerator)
            {
                if (evt == WatchEventType.Bookmark) { continue; } // Bookmarks do not contain any data.

                var srvResource = needAddressAllocated.Where(sr => sr.Service.Metadata.Name == updated.Metadata.Name).FirstOrDefault();
                if (srvResource == null) { continue; } // This service most likely already has full address information, so it is not on needAddressAllocated list.

                if (updated.HasCompleteAddress || updated.Spec.AddressAllocationMode == AddressAllocationModes.Proxyless)
                {
                    srvResource.Service.ApplyAddressInfoFrom(updated);
                    needAddressAllocated.Remove(srvResource);
                }

                if (needAddressAllocated.Count == 0)
                {
                    return; // We are done
                }
            }
        }
        finally
        {
            AspireEventSource.Instance.DcpServicesCreationStop();
        }
    }

    private async Task CreateContainersAndExecutablesAsync(CancellationToken cancellationToken)
    {
        var toCreate = _appResources.Where(r => r.DcpResource is Container || r.DcpResource is Executable || r.DcpResource is ExecutableReplicaSet);
        AddAllocatedEndpointInfo(toCreate);

        foreach (var lifecycleHook in _lifecycleHooks)
        {
            await lifecycleHook.AfterEndpointsAllocatedAsync(_model, cancellationToken).ConfigureAwait(false);
        }

        var containersTask = CreateContainersAsync(toCreate.Where(ar => ar.DcpResource is Container), cancellationToken);
        var executablesTask = CreateExecutablesAsync(toCreate.Where(ar => ar.DcpResource is Executable || ar.DcpResource is ExecutableReplicaSet), cancellationToken);

        await Task.WhenAll(containersTask, executablesTask).ConfigureAwait(false);
    }

    private static void AddAllocatedEndpointInfo(IEnumerable<AppResource> resources)
    {
        foreach (var appResource in resources)
        {
            foreach (var sp in appResource.ServicesProduced)
            {
                var svc = (Service)sp.DcpResource;

                if (!svc.HasCompleteAddress && sp.EndpointAnnotation.IsProxied)
                {
                    // This should never happen; if it does, we have a bug without a workaround for th the user.
                    throw new InvalidDataException($"Service {svc.Metadata.Name} should have valid address at this point");
                }

                if (!sp.EndpointAnnotation.IsProxied && svc.AllocatedPort is null)
                {
                    throw new InvalidOperationException($"Service '{svc.Metadata.Name}' needs to specify a port for endpoint '{sp.EndpointAnnotation.Name}' since it isn't using a proxy.");
                }

                var a = new AllocatedEndpointAnnotation(
                    sp.EndpointAnnotation.Name,
                    PortProtocol.ToProtocolType(svc.Spec.Protocol),
                    sp.EndpointAnnotation.IsProxied ? svc.AllocatedAddress! : "localhost",
                    (int)svc.AllocatedPort!,
                    sp.EndpointAnnotation.UriScheme
                    );

                appResource.ModelResource.Annotations.Add(a);
            }
        }
    }

    private void PrepareServices()
    {
        var serviceProducers = _model.Resources
            .Select(r => (ModelResource: r, SBAnnotations: r.Annotations.OfType<EndpointAnnotation>()))
            .Where(sp => sp.SBAnnotations.Any());

        // We need to ensure that Services have unique names (otherwise we cannot really distinguish between
        // services produced by different resources).
        HashSet<string> serviceNames = [];

        void addServiceAppResource(Service svc, IResource producingResource, EndpointAnnotation sba)
        {
            svc.Spec.Protocol = PortProtocol.FromProtocolType(sba.Protocol);
            svc.Annotate(CustomResource.UriSchemeAnnotation, sba.UriScheme);
            svc.Spec.AddressAllocationMode = sba.IsProxied ? AddressAllocationModes.Localhost : AddressAllocationModes.Proxyless;
            _appResources.Add(new ServiceAppResource(producingResource, svc, sba));
        }

        foreach (var sp in serviceProducers)
        {
            var sbAnnotations = sp.SBAnnotations.ToArray();

            foreach (var sba in sbAnnotations)
            {
                var candidateServiceName = sbAnnotations.Length == 1 ?
                    GetObjectNameForResource(sp.ModelResource) : GetObjectNameForResource(sp.ModelResource, sba.Name);
                var uniqueServiceName = GenerateUniqueServiceName(serviceNames, candidateServiceName);
                var svc = Service.Create(uniqueServiceName);

                int? port = _options.Value.RandomizePorts is true && sba.IsProxied ? null : sba.Port;
                svc.Spec.Port = port;

                addServiceAppResource(svc, sp.ModelResource, sba);
            }
        }
    }

    private void PrepareExecutables()
    {
        PrepareProjectExecutables();
        PreparePlainExecutables();
    }

    private void PreparePlainExecutables()
    {
        var modelExecutableResources = _model.GetExecutableResources();

        foreach (var executable in modelExecutableResources)
        {
            var exeName = GetObjectNameForResource(executable);
            var exePath = executable.Command;
            var exe = Executable.Create(exeName, exePath);

            // The working directory is always relative to the app host project directory (if it exists).
            exe.Spec.WorkingDirectory = executable.WorkingDirectory;
            exe.Spec.Args = executable.Args?.ToList();
            exe.Spec.ExecutionType = ExecutionType.Process;
            exe.Annotate(Executable.OtelServiceNameAnnotation, exe.Metadata.Name);
            exe.Annotate(Executable.ResourceNameAnnotation, executable.Name);

            var exeAppResource = new AppResource(executable, exe);
            AddServicesProducedInfo(executable, exe, exeAppResource);
            _appResources.Add(exeAppResource);
        }
    }

    private void PrepareProjectExecutables()
    {
        var modelProjectResources = _model.GetProjectResources();

        foreach (var project in modelProjectResources)
        {
            if (!project.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
            {
                throw new InvalidOperationException("A project resource is missing required metadata"); // Should never happen.
            }

            int replicas = project.GetReplicaCount();

            var ers = ExecutableReplicaSet.Create(GetObjectNameForResource(project), replicas, "dotnet");
            var exeSpec = ers.Spec.Template.Spec;
            IAnnotationHolder annotationHolder = ers.Spec.Template;

            exeSpec.WorkingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath);

            annotationHolder.Annotate(Executable.CSharpProjectPathAnnotation, projectMetadata.ProjectPath);
            annotationHolder.Annotate(Executable.OtelServiceNameAnnotation, ers.Metadata.Name);
            annotationHolder.Annotate(Executable.ResourceNameAnnotation, project.Name);

            if (!string.IsNullOrEmpty(configuration[DebugSessionPortVar]))
            {
                exeSpec.ExecutionType = ExecutionType.IDE;

                // ExcludeLaunchProfileAnnotation takes precedence over LaunchProfileAnnotation.
                if (project.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _))
                {
                    annotationHolder.Annotate(Executable.CSharpDisableLaunchProfileAnnotation, "true");
                }
                else if (project.TryGetLastAnnotation<LaunchProfileAnnotation>(out var lpa))
                {
                    annotationHolder.Annotate(Executable.CSharpLaunchProfileAnnotation, lpa.LaunchProfileName);
                }
            }
            else
            {
                exeSpec.ExecutionType = ExecutionType.Process;
                if (configuration.GetBool("DOTNET_WATCH") is not true)
                {
                    exeSpec.Args = [
                        "run",
                        "--no-build",
                        "--project",
                        projectMetadata.ProjectPath,
                    ];
                }
                else
                {
                    exeSpec.Args = [
                        "watch",
                        "--non-interactive",
                        "--no-hot-reload",
                        "--project",
                        projectMetadata.ProjectPath
                    ];
                }

                // We pretty much always want to suppress the normal launch profile handling
                // because the settings from the profile will override the ambient environment settings, which is not what we want
                // (the ambient environment settings for service processes come from the application model
                // and should be HIGHER priority than the launch profile settings).
                // This means we need to apply the launch profile settings manually--the invocation parameters here,
                // and the environment variables/application URLs inside CreateExecutableAsync().
                exeSpec.Args.Add("--no-launch-profile");

                var launchProfileName = project.SelectLaunchProfileName();
                if (!string.IsNullOrEmpty(launchProfileName))
                {
                    var launchProfile = project.GetEffectiveLaunchProfile();
                    if (launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs))
                    {
                        var cmdArgs = launchProfile.CommandLineArgs.Split((string?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (cmdArgs is not null && cmdArgs.Length > 0)
                        {
                            exeSpec.Args.Add("--");
                            exeSpec.Args.AddRange(cmdArgs);
                        }
                    }
                }
            }

            var exeAppResource = new AppResource(project, ers);
            AddServicesProducedInfo(project, annotationHolder, exeAppResource);
            _appResources.Add(exeAppResource);
        }
    }

    private Task CreateExecutablesAsync(IEnumerable<AppResource> executableResources, CancellationToken cancellationToken)
    {
        try
        {
            AspireEventSource.Instance.DcpExecutablesCreateStart();

            // Hoisting the aspire-dashboard resource if it exists to the top of
            // the list so we start it first.
            var sortedExecutableResources = executableResources.ToList();
            var (dashboardIndex, dashboardAppResource) = sortedExecutableResources.IndexOf(static r => StringComparers.ResourceName.Equals(r.ModelResource.Name, KnownResourceNames.AspireDashboard));

            if (dashboardIndex > 0)
            {
                sortedExecutableResources.RemoveAt(dashboardIndex);
                sortedExecutableResources.Insert(0, dashboardAppResource);
            }

            async Task CreateExecutableAsyncCore(AppResource cr, CancellationToken cancellationToken)
            {
                var logger = loggerService.GetLogger(cr.ModelResource);

                await notificationService.PublishUpdateAsync(cr.ModelResource, s => s with
                {
                    ResourceType = cr.ModelResource is ProjectResource ? KnownResourceTypes.Project : KnownResourceTypes.Executable,
                    Properties = [],
                    State = "Starting"
                })
                .ConfigureAwait(false);

                try
                {
                    await CreateExecutableAsync(cr, logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create resource {ResourceName}", cr.ModelResource.Name);

                    await notificationService.PublishUpdateAsync(cr.ModelResource, s => s with { State = "FailedToStart" }).ConfigureAwait(false);
                }
            }

            var tasks = new List<Task>();
            foreach (var er in sortedExecutableResources)
            {
                tasks.Add(CreateExecutableAsyncCore(er, cancellationToken));
            }

            return Task.WhenAll(tasks);
        }
        finally
        {
            AspireEventSource.Instance.DcpExecutablesCreateStop();
        }
    }

    private async Task CreateExecutableAsync(AppResource er, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        ExecutableSpec spec;
        Func<Task<CustomResource>> createResource;

        switch (er.DcpResource)
        {
            case Executable exe:
                spec = exe.Spec;
                createResource = async () => await kubernetesService.CreateAsync(exe, cancellationToken).ConfigureAwait(false);
                break;
            case ExecutableReplicaSet ers:
                spec = ers.Spec.Template.Spec;
                createResource = async () => await kubernetesService.CreateAsync(ers, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Expected an Executable-like resource, but got {er.DcpResource.Kind} instead");
        }

        spec.Args ??= [];

        if (er.ModelResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var exeArgsCallbacks))
        {
            var commandLineContext = new CommandLineArgsCallbackContext(spec.Args, cancellationToken);

            foreach (var exeArgsCallback in exeArgsCallbacks)
            {
                await exeArgsCallback.Callback(commandLineContext).ConfigureAwait(false);
            }
        }

        var config = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(_executionContext, config, cancellationToken)
        {
            Logger = resourceLogger
        };

        // Need to apply configuration settings manually; see PrepareExecutables() for details.
        if (er.ModelResource is ProjectResource project && project.SelectLaunchProfileName() is { } launchProfileName && project.GetLaunchSettings() is { } launchSettings)
        {
            ApplyLaunchProfile(er, config, launchProfileName, launchSettings);
        }
        else
        {
            if (er.ServicesProduced.Count > 0)
            {
                if (er.ModelResource is ProjectResource)
                {
                    var urls = er.ServicesProduced.Where(IsUnspecifiedHttpService).Select(sar =>
                    {
                        var url = sar.EndpointAnnotation.UriScheme + "://localhost:{{- portForServing \"" + sar.Service.Metadata.Name + "\" -}}";
                        return url;
                    });

                    // REVIEW: Should we assume ASP.NET Core?
                    // We're going to use http and https urls as ASPNETCORE_URLS
                    config["ASPNETCORE_URLS"] = string.Join(";", urls);
                }

                InjectPortEnvVars(er, config);
            }
        }

        if (er.ModelResource.TryGetEnvironmentVariables(out var envVarAnnotations))
        {
            foreach (var ann in envVarAnnotations)
            {
                await ann.Callback(context).ConfigureAwait(false);
            }
        }

        spec.Env = [];
        foreach (var c in config)
        {
            var value = c.Value switch
            {
                string s => s,
                IValueProvider valueProvider => await GetValue(c.Key, valueProvider, resourceLogger, isContainer: false, cancellationToken).ConfigureAwait(false),
                null => null,
                _ => throw new InvalidOperationException($"Unexpected value for environment variable \"{c.Key}\".")
            };

            if (value is not null)
            {
                spec.Env.Add(new EnvVar { Name = c.Key, Value = value });
            }
        }

        await createResource().ConfigureAwait(false);

        // NOTE: This check is only necessary for the inner loop in the dotnet/aspire repo. When
        //       running in the dotnet/aspire repo we will normally launch the dashboard via
        //       AddProject<T>. When doing this we make sure that the dashboard is running.
        if (!distributedApplicationOptions.DisableDashboard && er.ModelResource.Name.Equals(KnownResourceNames.AspireDashboard, StringComparisons.ResourceName))
        {
            // We just check the HTTP endpoint because this will prove that the
            // dashboard is listening and is ready to process requests.
            if (configuration["ASPNETCORE_URLS"] is not { } dashboardUrls)
            {
                throw new DistributedApplicationException("Cannot check dashboard availability since ASPNETCORE_URLS environment variable not set.");
            }

            PrintDashboardUrls(dashboardUrls);
        }
    }

    private async Task<string?> GetValue(string key, IValueProvider valueProvider, ILogger logger, bool isContainer, CancellationToken cancellationToken)
    {
        var task = valueProvider.GetValueAsync(cancellationToken);

        if (!task.IsCompleted)
        {
            if (valueProvider is IResource resource)
            {
                logger.LogInformation("Waiting for value for environment variable value '{Name}' from resource '{ResourceName}'", key, resource.Name);
            }
            else if (valueProvider is ConnectionStringReference { Resource: var cs })
            {
                logger.LogInformation("Waiting for value for connection string from resource '{ResourceName}'", cs.Name);
            }
            else
            {
                logger.LogInformation("Waiting for value for environment variable value '{Name}' from {Name}.", key, valueProvider.ToString());
            }
        }

        var value = await task.ConfigureAwait(false);

        if (value is not null && isContainer && valueProvider is ConnectionStringReference or EndpointReference)
        {
            // If the value is a connection string or endpoint reference, we need to replace localhost with the container host.
            return HostNameResolver.ReplaceLocalhostWithContainerHost(value, configuration);
        }

        return value;
    }

    private static void ApplyLaunchProfile(AppResource executableResource, Dictionary<string, object> config, string launchProfileName, LaunchSettings launchSettings)
    {
        // Populate DOTNET_LAUNCH_PROFILE environment variable for consistency with "dotnet run" and "dotnet watch".
        config.Add("DOTNET_LAUNCH_PROFILE", launchProfileName);

        var launchProfile = launchSettings.Profiles[launchProfileName];
        if (!string.IsNullOrWhiteSpace(launchProfile.ApplicationUrl))
        {
            if (executableResource.DcpResource is ExecutableReplicaSet)
            {
                var urls = executableResource.ServicesProduced.Where(IsUnspecifiedHttpService).Select(sar =>
                {
                    var url = sar.EndpointAnnotation.UriScheme + "://localhost:{{- portForServing \"" + sar.Service.Metadata.Name + "\" -}}";
                    return url;
                });

                config.Add("ASPNETCORE_URLS", string.Join(";", urls));
            }
            else
            {
                config.Add("ASPNETCORE_URLS", launchProfile.ApplicationUrl);
            }

            InjectPortEnvVars(executableResource, config);
        }

        foreach (var envVar in launchProfile.EnvironmentVariables)
        {
            string value = Environment.ExpandEnvironmentVariables(envVar.Value);
            config[envVar.Key] = value;
        }
    }

    private static void InjectPortEnvVars(AppResource executableResource, Dictionary<string, object> config)
    {
        ServiceAppResource? httpsServiceAppResource = null;
        // Inject environment variables for services produced by this executable.
        foreach (var serviceProduced in executableResource.ServicesProduced)
        {
            var name = serviceProduced.Service.Metadata.Name;
            var envVar = serviceProduced.EndpointAnnotation.EnvironmentVariable;

            if (envVar is not null)
            {
                config.Add(envVar, $"{{{{- portForServing \"{name}\" }}}}");
            }

            if (httpsServiceAppResource is null && serviceProduced.EndpointAnnotation.UriScheme == "https")
            {
                httpsServiceAppResource = serviceProduced;
            }
        }

        // REVIEW: If you run as an executable, we don't know that you're an ASP.NET Core application so we don't want to
        // inject ASPNETCORE_HTTPS_PORT.
        if (executableResource.ModelResource is ProjectResource)
        {
            // Add the environment variable for the HTTPS port if we have an HTTPS service. This will make sure the
            // HTTPS redirection middleware avoids redirecting to the internal port.
            if (httpsServiceAppResource is not null)
            {
                config.Add("ASPNETCORE_HTTPS_PORT", $"{{{{- portFor \"{httpsServiceAppResource.Service.Metadata.Name}\" }}}}");
            }
        }
    }

    private void PrepareContainers()
    {
        var modelContainerResources = _model.GetContainerResources();

        foreach (var container in modelContainerResources)
        {
            if (!container.TryGetContainerImageName(out var containerImageName))
            {
                // This should never happen! In order to get into this loop we need
                // to have the annotation, if we don't have the annotation by the time
                // we get here someone is doing something wrong.
                throw new InvalidOperationException();
            }

            var ctr = Container.Create(GetObjectNameForResource(container), containerImageName);

            ctr.Annotate(Container.ResourceNameAnnotation, container.Name);
            ctr.Annotate(Container.OtelServiceNameAnnotation, container.Name);

            if (container.TryGetContainerMounts(out var containerMounts))
            {
                ctr.Spec.VolumeMounts = new();

                foreach (var mount in containerMounts)
                {
                    var isBound = mount.Type == ContainerMountType.Bind;
                    var resolvedSource = mount.Source;
                    if (isBound)
                    {
                        // Source is only optional for creating anonymous volume mounts.
                        if (mount.Source == null)
                        {
                            throw new InvalidDataException($"Bind mount for container '{container.Name}' is missing required source.");
                        }

                        if (!Path.IsPathRooted(mount.Source))
                        {
                            resolvedSource = Path.GetFullPath(mount.Source);
                        }
                    }

                    var volumeSpec = new VolumeMount
                    {
                        Source = resolvedSource,
                        Target = mount.Target,
                        Type = isBound ? VolumeMountType.Bind : VolumeMountType.Volume,
                        IsReadOnly = mount.IsReadOnly
                    };
                    ctr.Spec.VolumeMounts.Add(volumeSpec);
                }
            }

            var containerAppResource = new AppResource(container, ctr);
            AddServicesProducedInfo(container, ctr, containerAppResource);
            _appResources.Add(containerAppResource);
        }
    }

    private Task CreateContainersAsync(IEnumerable<AppResource> containerResources, CancellationToken cancellationToken)
    {
        try
        {
            AspireEventSource.Instance.DcpContainersCreateStart();

            async Task CreateContainerAsyncCore(AppResource cr, CancellationToken cancellationToken)
            {
                var logger = loggerService.GetLogger(cr.ModelResource);

                await notificationService.PublishUpdateAsync(cr.ModelResource, s => s with
                {
                    State = "Starting",
                    Properties = [], // TODO: Add image name
                    ResourceType = KnownResourceTypes.Container
                })
                .ConfigureAwait(false);

                try
                {
                    await CreateContainerAsync(cr, logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create container resource {ResourceName}", cr.ModelResource.Name);

                    await notificationService.PublishUpdateAsync(cr.ModelResource, s => s with { State = "FailedToStart" }).ConfigureAwait(false);
                }
            }

            var tasks = new List<Task>();
            foreach (var cr in containerResources)
            {
                tasks.Add(CreateContainerAsyncCore(cr, cancellationToken));
            }

            return Task.WhenAll(tasks);
        }
        finally
        {
            AspireEventSource.Instance.DcpContainersCreateStop();
        }
    }

    private async Task CreateContainerAsync(AppResource cr, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var dcpContainerResource = (Container)cr.DcpResource;
        var modelContainerResource = cr.ModelResource;

        var config = new Dictionary<string, object>();

        dcpContainerResource.Spec.Env = [];

        if (cr.ServicesProduced.Count > 0)
        {
            dcpContainerResource.Spec.Ports = new();

            foreach (var sp in cr.ServicesProduced)
            {
                var portSpec = new ContainerPortSpec()
                {
                    ContainerPort = sp.DcpServiceProducerAnnotation.Port,
                };

                if (!sp.EndpointAnnotation.IsProxied)
                {
                    // When DCP isn't proxying the container we need to set the host port that the containers internal port will be mapped to
                    portSpec.HostPort = sp.EndpointAnnotation.Port;
                }

                if (!string.IsNullOrEmpty(sp.DcpServiceProducerAnnotation.Address))
                {
                    portSpec.HostIP = sp.DcpServiceProducerAnnotation.Address;
                }

                switch (sp.EndpointAnnotation.Protocol)
                {
                    case ProtocolType.Tcp:
                        portSpec.Protocol = PortProtocol.TCP; break;
                    case ProtocolType.Udp:
                        portSpec.Protocol = PortProtocol.UDP; break;
                }

                dcpContainerResource.Spec.Ports.Add(portSpec);

                var name = sp.Service.Metadata.Name;
                var envVar = sp.EndpointAnnotation.EnvironmentVariable;

                if (envVar is not null)
                {
                    config.Add(envVar, $"{{{{- portForServing \"{name}\" }}}}");
                }
            }
        }

        if (modelContainerResource.TryGetEnvironmentVariables(out var containerEnvironmentVariables))
        {
            var context = new EnvironmentCallbackContext(_executionContext, config, cancellationToken);

            foreach (var v in containerEnvironmentVariables)
            {
                await v.Callback(context).ConfigureAwait(false);
            }
        }

        foreach (var kvp in config)
        {
            var value = kvp.Value switch
            {
                string s => s,
                IValueProvider valueProvider => await GetValue(kvp.Key, valueProvider, resourceLogger, isContainer: true, cancellationToken).ConfigureAwait(false),
                null => null,
                _ => throw new InvalidOperationException($"Unexpected value for environment variable \"{kvp.Key}\".")
            };

            if (value is not null)
            {
                dcpContainerResource.Spec.Env.Add(new EnvVar { Name = kvp.Key, Value = value });
            }
        }

        if (modelContainerResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallback))
        {
            dcpContainerResource.Spec.Args ??= [];

            var commandLineArgsContext = new CommandLineArgsCallbackContext(dcpContainerResource.Spec.Args, cancellationToken);

            foreach (var callback in argsCallback)
            {
                await callback.Callback(commandLineArgsContext).ConfigureAwait(false);
            }
        }

        if (modelContainerResource is ContainerResource containerResource)
        {
            dcpContainerResource.Spec.Command = containerResource.Entrypoint;
        }

        await kubernetesService.CreateAsync(dcpContainerResource, cancellationToken).ConfigureAwait(false);
    }

    private void AddServicesProducedInfo(IResource modelResource, IAnnotationHolder dcpResource, AppResource appResource)
    {
        string modelResourceName = "(unknown)";
        try
        {
            modelResourceName = GetObjectNameForResource(modelResource);
        }
        catch { } // For error messages only, OK to fall back to (unknown)

        var servicesProduced = _appResources.OfType<ServiceAppResource>().Where(r => r.ModelResource == modelResource);
        foreach (var sp in servicesProduced)
        {
            // Projects/Executables have their ports auto-allocated; the port specified by the EndpointAnnotation
            // is applied to the Service objects and used by clients.
            // Containers use the port from the EndpointAnnotation directly.

            if (modelResource.IsContainer())
            {
                if (sp.EndpointAnnotation.ContainerPort is null)
                {
                    throw new InvalidOperationException($"The endpoint for container resource {modelResourceName} must specify the ContainerPort");
                }

                sp.DcpServiceProducerAnnotation.Port = sp.EndpointAnnotation.ContainerPort;
            }
            else if (!sp.EndpointAnnotation.IsProxied)
            {
                if (appResource.DcpResource is ExecutableReplicaSet ers && ers.Spec.Replicas > 1)
                {
                    throw new InvalidOperationException($"'{modelResourceName}' specifies multiple replicas and at least one proxyless endpoint. These features do not work together.");
                }

                // DCP will not allocate a port for this proxyless service
                // so we need to specify what the port is so DCP is aware of it.
                sp.DcpServiceProducerAnnotation.Port = sp.EndpointAnnotation.Port;
            }

            dcpResource.AnnotateAsObjectList(CustomResource.ServiceProducerAnnotation, sp.DcpServiceProducerAnnotation);
            appResource.ServicesProduced.Add(sp);
        }
    }

    private async Task CreateResourcesAsync<RT>(CancellationToken cancellationToken) where RT : CustomResource
    {
        try
        {
            var resourcesToCreate = _appResources.Select(r => r.DcpResource).OfType<RT>();
            if (!resourcesToCreate.Any())
            {
                return;
            }

            // CONSIDER batched creation
            foreach (var res in resourcesToCreate)
            {
                await kubernetesService.CreateAsync(res, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            // We catch and suppress the OperationCancelledException because the user may CTRL-C
            // during start up of the resources.
            _logger.LogDebug(ex, "Cancellation during creation of resources.");
        }
    }

    private string GetObjectNameForResource(IResource resource, string suffix = "")
    {
        static string maybeWithSuffix(string s, string localSuffix, string? globalSuffix)
            => (string.IsNullOrWhiteSpace(localSuffix), string.IsNullOrWhiteSpace(globalSuffix)) switch
            {
                (true, true) => s,
                (false, true) => $"{s}_{localSuffix}",
                (true, false) => $"{s}_{globalSuffix}",
                (false, false) => $"{s}_{localSuffix}_{globalSuffix}"
            };
        return maybeWithSuffix(resource.Name, suffix, _options.Value.ResourceNameSuffix);
    }

    private static string GenerateUniqueServiceName(HashSet<string> serviceNames, string candidateName)
    {
        int suffix = 1;
        string uniqueName = candidateName;

        while (!serviceNames.Add(uniqueName))
        {
            uniqueName = $"{candidateName}_{suffix}";
            suffix++;
            if (suffix == 100)
            {
                // Should never happen, but we do not want to ever get into a infinite loop situation either.
                throw new ArgumentException($"Could not generate a unique name for service '{candidateName}'");
            }
        }

        return uniqueName;
    }

    // Returns true if this resource represents an HTTP service endpoint which does not specify an environment variable for the endpoint.
    // This is used to decide whether the endpoint should be propagated via the ASPNETCORE_URLS environment variable.
    private static bool IsUnspecifiedHttpService(ServiceAppResource serviceAppResource)
    {
        return serviceAppResource.EndpointAnnotation is
        {
            UriScheme: "http" or "https",
            EnvironmentVariable: null or { Length: 0 }
        };
    }

    public async Task DeleteResourcesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AspireEventSource.Instance.DcpModelCleanupStart();
            await DeleteResourcesAsync<ExecutableReplicaSet>("project", cancellationToken).ConfigureAwait(false);
            await DeleteResourcesAsync<Executable>("project", cancellationToken).ConfigureAwait(false);
            await DeleteResourcesAsync<Container>("container", cancellationToken).ConfigureAwait(false);
            await DeleteResourcesAsync<Service>("service", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.DcpModelCleanupStop();
            _appResources.Clear();
        }
    }

    private async Task DeleteResourcesAsync<RT>(string resourceType, CancellationToken cancellationToken) where RT : CustomResource
    {
        var resourcesToDelete = _appResources.Select(r => r.DcpResource).OfType<RT>();
        if (!resourcesToDelete.Any())
        {
            return;
        }

        foreach (var res in resourcesToDelete)
        {
            try
            {
                await kubernetesService.DeleteAsync<RT>(res.Metadata.Name, res.Metadata.NamespaceProperty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Could not stop {ResourceType} '{ResourceName}'.", resourceType, res.Metadata.Name);
            }
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Tests.Utils;
using Xunit;

namespace Aspire.Hosting.Tests;

public class WithReferenceTests
{
    [Theory]
    [InlineData("mybinding")]
    [InlineData("MYbinding")]
    public async Task ResourceWithSingleEndpointProducesSimplifiedEnvironmentVariables(string endpointName)
    {
        using var testProgram = CreateTestProgram();

        // Create a binding and its metching annotation (simulating DCP behavior)
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 2000, "mybinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding",
            ProtocolType.Tcp,
            "localhost",
            2000,
            "https"
            ));

        // Get the service provider.
        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder.GetEndpoint(endpointName));
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(2, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__0" && kvp.Value == "mybinding://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__1" && kvp.Value == "https://localhost:2000");
    }

    [Fact]
    public async Task ResourceWithConflictingEndpointsProducesFullyScopedEnvironmentVariables()
    {
        using var testProgram = CreateTestProgram();

        // Create a binding and its matching annotation (simulating DCP behavior)
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 2000, "mybinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding",
            ProtocolType.Tcp,
            "localhost",
            2000,
            "https"
            ));

        // Create a binding and its matching annotation (simulating DCP behavior) - HOWEVER
        // this binding conflicts with the earlier because they have the same scheme.
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 3000, "myconflictingbinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("myconflictingbinding",
            ProtocolType.Tcp,
            "localhost",
            3000,
            "https"
            ));

        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder.GetEndpoint("mybinding"));
        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder.GetEndpoint("myconflictingbinding"));

        // Get the service provider.
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(2, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__0" && kvp.Value == "mybinding://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__1" && kvp.Value == "myconflictingbinding://localhost:3000");
    }

    [Fact]
    public async Task ResourceWithNonConflictingEndpointsProducesAllVariantsOfEnvironmentVariables()
    {
        using var testProgram = CreateTestProgram();

        // Create a binding and its matching annotation (simulating DCP behavior)
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 2000, "mybinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding",
            ProtocolType.Tcp,
            "localhost",
            2000,
            "https"
            ));

        // Create a binding and its matching annotation (simulating DCP behavior) - not
        // conflicting because the scheme is different to the first binding.
        testProgram.ServiceABuilder.WithHttpEndpoint(1000, 3000, "mynonconflictingbinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mynonconflictingbinding",
            ProtocolType.Tcp,
            "localhost",
            3000,
            "http"
            ));

        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder.GetEndpoint("mybinding"));
        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder.GetEndpoint("mynonconflictingbinding"));

        // Get the service provider.
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(4, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__0" && kvp.Value == "mybinding://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__1" && kvp.Value == "https://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__2" && kvp.Value == "mynonconflictingbinding://localhost:3000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__3" && kvp.Value == "http://localhost:3000");
    }

    [Fact]
    public async Task ResourceWithConflictingEndpointsProducesAllEnvironmentVariables()
    {
        using var testProgram = CreateTestProgram();

        // Create a binding and its metching annotation (simulating DCP behavior)
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 2000, "mybinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding",
            ProtocolType.Tcp,
            "localhost",
            2000,
            "https"
            ));

        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 3000, "mybinding2");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding2",
            ProtocolType.Tcp,
            "localhost",
            3000,
            "https"
            ));

        // Get the service provider.
        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(2, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__0" && kvp.Value == "mybinding://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__1" && kvp.Value == "mybinding2://localhost:3000");
    }

    [Fact]
    public async Task ResourceWithEndpointsProducesAllEnvironmentVariables()
    {
        using var testProgram = CreateTestProgram();

        // Create a binding and its metching annotation (simulating DCP behavior)
        testProgram.ServiceABuilder.WithHttpsEndpoint(1000, 2000, "mybinding");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding",
            ProtocolType.Tcp,
            "localhost",
            2000,
            "https"
            ));

        testProgram.ServiceABuilder.WithHttpEndpoint(1000, 3000, "mybinding2");
        testProgram.ServiceABuilder.WithAnnotation(
            new AllocatedEndpointAnnotation("mybinding2",
            ProtocolType.Tcp,
            "localhost",
            3000,
            "http"
            ));

        // Get the service provider.
        testProgram.ServiceBBuilder.WithReference(testProgram.ServiceABuilder);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(4, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__0" && kvp.Value == "mybinding://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__1" && kvp.Value == "https://localhost:2000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__2" && kvp.Value == "mybinding2://localhost:3000");
        Assert.Contains(config, kvp => kvp.Key == "services__servicea__3" && kvp.Value == "http://localhost:3000");
    }

    [Fact]
    public async Task ConnectionStringResourceThrowsWhenMissingConnectionString()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddResource(new TestResource("resource"));
        testProgram.ServiceBBuilder.WithReference(resource, optional: false);
        testProgram.Build();

        // Call environment variable callbacks.
        await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);
        });
    }

    [Fact]
    public async Task ConnectionStringResourceOptionalWithMissingConnectionString()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddResource(new TestResource("resource"));
        testProgram.ServiceBBuilder.WithReference(resource, optional: true);
        testProgram.Build();

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(0, servicesKeysCount);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceThrowsWhenConnectionStringSectionMissing()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var missingResource = testProgram.AppBuilder.AddConnectionString("missingresource");
        testProgram.ServiceBBuilder.WithReference(missingResource);
        testProgram.Build();

        // Call environment variable callbacks.
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);
        });

        Assert.Equal("Connection string parameter resource could not be used because connection string 'missingresource' is missing.", exception.Message);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsConnectionStringWhenPresent()
    {
        using var testProgram = CreateTestProgram();
        testProgram.AppBuilder.Configuration["ConnectionStrings:resource"] = "test connection string";

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddConnectionString("resource");
        testProgram.ServiceBBuilder.WithReference(resource);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        Assert.Equal("test connection string", config["ConnectionStrings__resource"]);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsExpressionWhenPublishingManifest()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddConnectionString("resource");
        testProgram.ServiceBBuilder.WithReference(resource);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("{resource.connectionString}", config["ConnectionStrings__resource"]);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsCorrectEnvWhenPublishingManifest()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddConnectionString("resource", "MY_ENV");
        testProgram.ServiceBBuilder.WithReference(resource);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("{resource.connectionString}", config["MY_ENV"]);
    }

    [Fact]
    public async Task ConnectionStringResourceWithConnectionString()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddResource(new TestResource("resource")
        {
            ConnectionString = "123"
        });
        testProgram.ServiceBBuilder.WithReference(resource);
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "123");
    }

    [Fact]
    public async Task ConnectionStringResourceWithConnectionStringOverwriteName()
    {
        using var testProgram = CreateTestProgram();

        // Get the service provider.
        var resource = testProgram.AppBuilder.AddResource(new TestResource("resource")
        {
            ConnectionString = "123"
        });
        testProgram.ServiceBBuilder.WithReference(resource, connectionName: "bob");
        testProgram.Build();

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceBBuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__bob" && kvp.Value == "123");
    }

    [Fact]
    public void WithReferenceHttpRelativeUriThrowsException()
    {
        using var testProgram = CreateTestProgram();

        Assert.Throws<InvalidOperationException>(() => testProgram.ServiceABuilder.WithReference("petstore", new Uri("petstore.swagger.io", UriKind.Relative)));
    }

    [Fact]
    public void WithReferenceHttpUriThrowsException()
    {
        using var testProgram = CreateTestProgram();

        Assert.Throws<InvalidOperationException>(() => testProgram.ServiceABuilder.WithReference("petstore", new Uri("https://petstore.swagger.io/v2")));
    }

    [Fact]
    public async Task WithReferenceHttpProduceEnvironmentVariables()
    {
        using var testProgram = CreateTestProgram();

        testProgram.ServiceABuilder.WithReference("petstore", new Uri("https://petstore.swagger.io/"));

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(testProgram.ServiceABuilder.Resource);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__petstore" && kvp.Value == "https://petstore.swagger.io/");
    }

    private static TestProgram CreateTestProgram(string[]? args = null) => TestProgram.Create<WithReferenceTests>(args);

    private sealed class TestResource(string name) : IResourceWithConnectionString
    {
        public string Name => name;

        public string? ConnectionString { get; set; }

        public ResourceAnnotationCollection Annotations => throw new NotImplementedException();

        public string? GetConnectionString()
        {
            return ConnectionString;
        }
    }
}

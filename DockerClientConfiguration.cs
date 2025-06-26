using Docker.DotNet;

public static class DockerClientConfigurationExtensions
{
    public static IServiceCollection AddDockerClient(this IServiceCollection services, IConfiguration configuration)
    {
        var dockerEndpoint = configuration.GetValue<string>("Docker:EndpointUri");

        if (string.IsNullOrEmpty(dockerEndpoint))
        {
            dockerEndpoint = OperatingSystem.IsWindows()
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock"; 
        }

        Console.WriteLine($"Using Docker Endpoint: {dockerEndpoint}"); 

        services.AddSingleton<IDockerClient>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DockerClient>>();
            try
            {
                var client = new DockerClientConfiguration(new Uri(dockerEndpoint))
                                 .CreateClient();

                logger.LogInformation("Successfully created DockerClient connected to {Endpoint}", dockerEndpoint);
                return client;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Failed to create DockerClient connected to {Endpoint}. Ensure Docker daemon is running and accessible.", dockerEndpoint);
                throw new InvalidOperationException($"Failed to initialize Docker client at {dockerEndpoint}", ex);
            }
        });

        return services;
    }
}
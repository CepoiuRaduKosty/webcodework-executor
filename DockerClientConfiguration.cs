// DockerClientConfiguration.cs
using Docker.DotNet;

public static class DockerClientConfigurationExtensions
{
    public static IServiceCollection AddDockerClient(this IServiceCollection services, IConfiguration configuration)
    {
        // Determine Docker endpoint based on OS or configuration
        // Default for Linux: unix:///var/run/docker.sock
        // Default for Windows: npipe://./pipe/docker_engine
        // Can be overridden via configuration if needed
        var dockerEndpoint = configuration.GetValue<string>("Docker:EndpointUri");

        if (string.IsNullOrEmpty(dockerEndpoint))
        {
            dockerEndpoint = OperatingSystem.IsWindows()
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock"; // Common Linux path
        }

        Console.WriteLine($"Using Docker Endpoint: {dockerEndpoint}"); // Log the endpoint being used

        services.AddSingleton<IDockerClient>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DockerClient>>();
            try
            {
                 // Credentials argument is typically null for local daemon sockets/pipes
                var client = new DockerClientConfiguration(new Uri(dockerEndpoint))
                                 .CreateClient();

                // Optional: Ping the Docker daemon to ensure connection on startup
                // client.System.PingAsync().Wait(); // Or use async factory pattern
                logger.LogInformation("Successfully created DockerClient connected to {Endpoint}", dockerEndpoint);
                return client;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Failed to create DockerClient connected to {Endpoint}. Ensure Docker daemon is running and accessible.", dockerEndpoint);
                // Decide how to handle this - throw, return null, use a dummy client?
                // Throwing is often best during startup.
                throw new InvalidOperationException($"Failed to initialize Docker client at {dockerEndpoint}", ex);
            }
        });

        return services;
    }
}
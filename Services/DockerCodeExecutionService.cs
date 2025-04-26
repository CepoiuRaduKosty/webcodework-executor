// Services/DockerCodeExecutionService.cs
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Threading;
using System.Threading.Tasks;

namespace WebCodeWorkExecutor.Services
{
    public class DockerCodeExecutionService : ICodeExecutionService
    {
        private readonly IDockerClient _dockerClient;
        private readonly ILogger<DockerCodeExecutionService> _logger;

        public DockerCodeExecutionService(IDockerClient dockerClient, ILogger<DockerCodeExecutionService> logger)
        {
            _dockerClient = dockerClient;
            _logger = logger;
        }

        public async Task<bool> TestContainerLifecycleAsync(string imageName = "alpine:latest")
        {
            string? containerId = null;
            try
            {
                _logger.LogInformation("Attempting to test container lifecycle with image: {ImageName}", imageName);

                // 1. Ensure Image Exists Locally (Pull if necessary)
                // Note: In production, you might pre-pull images or use a private registry
                await _dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = imageName, Tag = "latest" }, // Adjust tag if needed
                    null, // No auth config needed for public images
                    new Progress<JSONMessage>(m => _logger.LogDebug("Pull progress: {Status}", m.Status))); // Log progress


                // 2. Create Container Configuration
                var config = new Config
                {
                    Image = imageName,
                    Cmd = new List<string> { "echo", "Hello from container!" }, // Simple command
                    Tty = false, // No interactive TTY needed for basic command
                    AttachStdout = true, // We want to capture output later
                    AttachStderr = true,
                };

                var hostConfig = new HostConfig
                {
                    // Resource Limits (IMPORTANT FOR SECURITY/STABILITY - SET LATER)
                    // Memory = 256 * 1024 * 1024, // Example: 256 MB RAM limit
                    // NanoCPUs = 1 * 1_000_000_000, // Example: Limit to 1 CPU core

                    // Network Mode (IMPORTANT FOR SECURITY)
                    NetworkMode = "none", // Isolate network initially unless needed

                    // Volume Mounts (IMPLEMENT LATER FOR CODE/INPUT)
                    // Mounts = new List<Mount> { ... }

                    // AutoRemove container when it exits (optional, good for cleanup)
                    AutoRemove = true
                };

                // 3. Create the Container
                _logger.LogInformation("Creating container from image {ImageName}...", imageName);
                var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                    new CreateContainerParameters(config)
                    {
                        HostConfig = hostConfig,
                        // Name = $"test-container-{Guid.NewGuid()}" // Optional name
                    });
                containerId = createResponse.ID;
                _logger.LogInformation("Container created with ID: {ContainerId}", containerId);


                // 4. Start the Container
                _logger.LogInformation("Starting container {ContainerId}...", containerId);
                await _dockerClient.Containers.StartContainerAsync(containerId, null); // null = default start params


                // 5. Wait for Container to Exit (Simple Command Exits Quickly)
                _logger.LogInformation("Waiting for container {ContainerId} to complete...", containerId);
                var waitResponse = await _dockerClient.Containers.WaitContainerAsync(containerId);
                _logger.LogInformation("Container {ContainerId} exited with status code: {StatusCode}", containerId, waitResponse.StatusCode);

                // --- In a real scenario, you would capture logs/output here ---
                // Example (Needs more robust stream handling):
                // var logs = await _dockerClient.Containers.GetContainerLogsAsync(containerId, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true });
                // using var reader = new StreamReader(logs);
                // string output = await reader.ReadToEndAsync();
                // _logger.LogInformation("Container Output:\n{Output}", output);
                // -------------------------------------------------------------

                // 6. Container should AutoRemove if HostConfig.AutoRemove = true
                // If not using AutoRemove, you would manually stop and remove:
                // _logger.LogInformation("Stopping container {ContainerId}...", containerId);
                // await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
                // _logger.LogInformation("Removing container {ContainerId}...", containerId);
                // await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });


                return waitResponse.StatusCode == 0; // Success if exit code is 0
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during container lifecycle test for image {ImageName}. Container ID (if created): {ContainerId}", imageName, containerId ?? "N/A");
                // Attempt cleanup if container was created but didn't auto-remove
                if (!string.IsNullOrEmpty(containerId))
                {
                    try
                    {
                         _logger.LogWarning("Attempting cleanup of container {ContainerId} after error.", containerId);
                        // Force remove might be needed if stop fails or AutoRemove wasn't set/didn't work
                         await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
                         _logger.LogInformation("Cleanup successful for container {ContainerId}.", containerId);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogError(cleanupEx, "Failed to cleanup container {ContainerId} after error.", containerId);
                    }
                }
                return false; // Indicate failure
            }
        }

        // --- Implement ExecuteCodeAsync later ---
    }
}
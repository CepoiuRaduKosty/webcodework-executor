using Docker.DotNet;
using Docker.DotNet.Models;
using System.Net.Sockets; 
using System.Net; 
using System.Net.Http.Headers;
using WebCodeWorkExecutor.Dtos;
using System.Diagnostics;

namespace WebCodeWorkExecutor.Services
{
    public class DockerCodeExecutionService : ICodeExecutionService
    {
        private readonly IDockerClient _dockerClient;
        private readonly IHttpClientFactory _httpClientFactory; 
        private readonly IConfiguration _configuration; 
        private readonly ILogger<DockerCodeExecutionService> _logger;
        private readonly IEvaluationContainerService _evaluationContainerService;

        public DockerCodeExecutionService(
            IDockerClient dockerClient,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<DockerCodeExecutionService> logger,
            IEvaluationContainerService evaluationContainerService)
        {
            _dockerClient = dockerClient;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _evaluationContainerService = evaluationContainerService;
        }

        public async Task<string?> StartCodeEvalAsync(
            string language,
            string codeFilePath,
            int submissionId,
            List<TestCaseInfo> testCases,
            Action<Exception> onException)
        {
            string? containerId = null;
            try
            {
                var runnerImageName = _configuration.GetValue<string>($"RunnerImages:{language.ToLowerInvariant()}")!;
                _logger.LogInformation("Starting batch evaluation for {TestCaseCount} test cases. Language: {Language}, Solution: {CodePath}, Runner Image: {Image}",
                testCases.Count, language, codeFilePath, runnerImageName);

                await EnsureImageExistsAsync(runnerImageName);
                (int port, containerId) = await CreateContainer(language, runnerImageName);

                _logger.LogInformation("Starting batch runner container ({ContainerId})...", containerId);
                bool startResult = await _dockerClient.Containers.StartContainerAsync(containerId, null);
                if (!startResult)
                    throw new Exception("Could not start container");

                if (!await WaitForHealthyContainer(port, _configuration.GetValue<int>("Containers:MaxWaitForStartSec")))
                        throw new Exception("Timeout on container boot");

                _logger.LogInformation("Sending batch evaluation request to runner {ContainerId} via port {HostPort}...", containerId, port);
                var result = await _evaluationContainerService.StartEvaluation(language, codeFilePath, submissionId, testCases, port);
                if (!result)
                    throw new Exception("Could not start evaluation");
                return containerId;
            }
            catch (Exception ex)
            {
                onException(ex);
                await CleanContainer(containerId);
                return null;
            }
        }

        private async Task CleanContainer(string? containerId)
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                _logger.LogDebug("Stopping batch runner container {ContainerId}...", containerId);
                try
                {
                    await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 3 });
                    _logger.LogInformation("Stopped batch runner container {ContainerId}.", containerId);
                }
                catch (Exception cleanupEx) { _logger.LogError(cleanupEx, "Error stopping/cleaning up batch runner container {ContainerId}.", containerId); }
            }
        }

        public async Task ForceStopEvaluation(string id)
        {
            await CleanContainer(id);
        }

        private async Task<bool> WaitForHealthyContainer(int port, int timeoutSeconds)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            while (stopwatch.Elapsed < timeout)
            {
                if (await _evaluationContainerService.HealthCheck(port))
                    return true;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            return false;
        }

        private async Task<(int hostport, string containerId)> CreateContainer(string language, string runnerImageName)
        {
            var languageLower = language.ToLowerInvariant();
            string containerName = $"batch-runner-{languageLower}-{GenerateRandomSuffix(6)}";
            int hostPort = GetFreeTcpPort();
            string internalRunnerPort = "5000";
            _logger.LogDebug("Obtained free host port {HostPort} for runner's internal port {InternalPort}", hostPort, internalRunnerPort);
            var envVars = new List<string> {
                $"Execution__Language={languageLower}",
                $"ASPNETCORE_URLS=http://+:{internalRunnerPort}",
            };
            var tmpfsMounts = new Dictionary<string, string>
            {
                { "/sandbox", $"size={_configuration.GetValue<long>("GlobalLimits:MaxStorageWorkdirMb") * 1024 * 1024},mode=1777" }
            };
            var portBindings = new Dictionary<string, IList<PortBinding>> {
                { $"{internalRunnerPort}/tcp", new List<PortBinding> { new PortBinding { HostPort = hostPort.ToString() } } }
            };
            var hostConfig = new HostConfig
            {
                PortBindings = portBindings,
                AutoRemove = true,
                NetworkMode = "bridge",
                Memory = _configuration.GetValue<int>("GlobalLimits:MaxMemoryMb") * 1024 * 1024,
                CPUPeriod = 100000,
                CPUQuota = (int)(100000 * _configuration.GetValue<double>("GlobalLimits:CpuQuotaCoeffPerContainer")),
                PidsLimit = _configuration.GetValue<int>("GlobalLimits:PidsLimit"),
                ReadonlyRootfs = true,
                Privileged = false,
                CapDrop = ["ALL"],
                CapAdd = ["CAP_KILL"],
                Tmpfs = tmpfsMounts
            };
            var containerConfig = new Config { Image = runnerImageName, Env = envVars };
            _logger.LogInformation("Creating batch runner container '{ContainerName}' on host port {HostPort}...", containerName, hostPort);
            var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                new CreateContainerParameters(containerConfig) { HostConfig = hostConfig, Name = containerName }
            );
            _logger.LogInformation($"Creating container warnings: {string.Join(' ', createResponse.Warnings)}");

            return (hostPort, createResponse.ID);
        }

        private string GenerateRandomSuffix(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private int GetFreeTcpPort()
        {
             var listener = new TcpListener(IPAddress.Loopback, 0);
             listener.Start();
             int port = ((IPEndPoint)listener.LocalEndpoint).Port;
             listener.Stop();
             _logger.LogDebug("Found free TCP port: {Port}", port);
             return port;
        }

        
        private async Task EnsureImageExistsAsync(string imageNameWithTag)
        {
            if (string.IsNullOrWhiteSpace(imageNameWithTag))
            {
                throw new ArgumentNullException(nameof(imageNameWithTag));
            }

            try
            {
                _logger.LogDebug("Checking if image {Image} exists locally...", imageNameWithTag);
                
                var imageListParameters = new ImagesListParameters
                {
                    
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> 
                        {
                            [imageNameWithTag] = true 
                        }
                    },
                    All = true 
                };

                var imageList = await _dockerClient.Images.ListImagesAsync(imageListParameters);
                

                if (!imageList.Any()) 
                {
                    _logger.LogInformation("Image {Image} not found locally. Pulling...", imageNameWithTag);
                    
                    string imageName = imageNameWithTag;
                    string tag = "latest"; 
                    if (imageNameWithTag.Contains(':'))
                    {
                        var parts = imageNameWithTag.Split(':');
                        imageName = parts[0];
                        tag = parts[1];
                    }
                    else
                    {
                        
                         _logger.LogDebug("No tag specified for {Image}, assuming 'latest'.", imageName);
                    }

                    await _dockerClient.Images.CreateImageAsync(
                        new ImagesCreateParameters { FromImage = imageName, Tag = tag },
                        null, 
                        new Progress<JSONMessage>(m => {})
                     );
                     _logger.LogInformation("Image {Image} pulled successfully.", imageNameWithTag);
                }
                else
                {
                    _logger.LogDebug("Image {Image} found locally.", imageNameWithTag);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure Docker image {Image} exists.", imageNameWithTag);
                
                throw; 
            }
        }
    }
}

using Docker.DotNet;
using Docker.DotNet.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets; 
using System.Net; 
using System.Text; 
using System.Text.Json; 
using System.Net.Http.Headers; 
using System.Collections.Generic; 
using System.Linq;
using WebCodeWorkExecutor.Authentication; 

namespace WebCodeWorkExecutor.Services
{
    
    public class DockerCodeExecutionService : ICodeExecutionService
    {
        private readonly IDockerClient _dockerClient;
        private readonly IHttpClientFactory _httpClientFactory; 
        private readonly IConfiguration _configuration; 
        private readonly ILogger<DockerCodeExecutionService> _logger;
        private readonly string _apiKey; 

        public DockerCodeExecutionService(
            IDockerClient dockerClient,
            IHttpClientFactory httpClientFactory, 
            IConfiguration configuration, 
            ILogger<DockerCodeExecutionService> logger)
        {
            _dockerClient = dockerClient;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _apiKey = _configuration.GetValue<string>("Authentication:ApiKey") ?? 
                      throw new InvalidOperationException("Runner API Key ('Authentication:RunnerApiKey') not configured in Orchestrator.");
        }

        public async Task<SolutionEvaluationResult> EvaluateSolutionAsync(
            string language,
            string codeFilePath,
            List<TestCaseInfo> testCases)
        {
            var overallResults = new List<TestCaseEvaluationResult>();
            var languageLower = language.ToLowerInvariant();
            var runnerImageName = _configuration.GetValue<string>($"RunnerImages:{languageLower}")
                                  ?? $"generic-runner-{languageLower}:latest"; 

            _logger.LogInformation("Starting batch evaluation for {TestCaseCount} test cases. Language: {Language}, Solution: {CodePath}, Runner Image: {Image}",
                testCases.Count, language, codeFilePath, runnerImageName);

            await EnsureImageExistsAsync(runnerImageName);

            string containerName = $"batch-runner-{languageLower}-{GenerateRandomSuffix(6)}";
            string? containerId = null;
            int hostPort = 0;
            HttpClient? httpClient = null;

            try
            {
                
                hostPort = GetFreeTcpPort();
                string internalRunnerPort = "5000";
                string hostPortStr = hostPort.ToString();
                _logger.LogDebug("Obtained free host port {HostPort} for runner's internal port {InternalPort}", hostPort, internalRunnerPort);

                var runnerBatchRequest = new RunnerBatchExecuteRequestDto
                {
                    Language = languageLower,
                    CodeFilePath = codeFilePath, 
                    TestCases = testCases.Select(tc => new RunnerTestCaseItemDto
                    {
                        InputFilePath = tc.InputFilePath,
                        ExpectedOutputFilePath = tc.ExpectedOutputFilePath,
                        TimeLimitMs = tc.MaxExecutionTimeMs,
                        MaxRamMB = tc.MaxRamMB,
                        TestCaseId = tc.TestCaseId 
                    }).ToList(),
                };

                
                var envVars = new List<string> {
                    $"Authentication__ApiKey={_apiKey}",
                    $"Execution__Language={languageLower}",
                    $"ASPNETCORE_URLS=http://+:{internalRunnerPort}",
                    $"AzureStorage__ConnectionString={_configuration.GetValue<string>("AzureStorage:ConnectionString")}",
                    $"AzureStorage__ContainerName={_configuration.GetValue<string>("AzureStorage:ContainerName")}"
                };

                
                var portBindings = new Dictionary<string, IList<PortBinding>> {
                    { $"{internalRunnerPort}/tcp", new List<PortBinding> { new PortBinding { HostPort = hostPort.ToString() } } }
                };

                var hostConfig = new HostConfig
                {
                    PortBindings = portBindings,
                    AutoRemove = true,
                    NetworkMode = "bridge"
                };

                var containerConfig = new Config { Image = runnerImageName, Env = envVars };

                _logger.LogInformation("Creating batch runner container '{ContainerName}' on host port {HostPort}...", containerName, hostPort);
                var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                    new CreateContainerParameters(containerConfig) { HostConfig = hostConfig, Name = containerName }
                );
                containerId = createResponse.ID;

                _logger.LogInformation("Starting batch runner container '{ContainerName}' ({ContainerId})...", containerName, containerId);
                await _dockerClient.Containers.StartContainerAsync(containerId, null);

                await Task.Delay(TimeSpan.FromSeconds(5)); 

                httpClient = _httpClientFactory.CreateClient("RunnerApiClient"); 
                httpClient.BaseAddress = new Uri($"http://localhost:{hostPort}");
                httpClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.ApiKeyHeaderName, _apiKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.Timeout = TimeSpan.FromMinutes(10);

                _logger.LogInformation("Sending batch evaluation request to runner {ContainerId} via port {HostPort}...", containerId, hostPort);
                
                HttpResponseMessage response = await httpClient.PostAsJsonAsync("/execute", runnerBatchRequest);

                RunnerBatchExecuteResponseDto? batchRunnerResult = null;
                if (response.IsSuccessStatusCode)
                {
                    batchRunnerResult = await response.Content.ReadFromJsonAsync<RunnerBatchExecuteResponseDto>();
                    _logger.LogInformation("Runner API batch call successful. Compilation: {CompileStatus}, Test Cases: {Count}",
                       batchRunnerResult?.CompilationSuccess, batchRunnerResult?.TestCaseResults?.Count ?? 0);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Runner API batch call failed. Status: {StatusCode}, Reason: {Reason}, Content: {Content}",
                        response.StatusCode, response.ReasonPhrase, errorContent);
                    
                    var errorResults = testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, $"Runner API communication failed: {response.StatusCode}", null, false)).ToList();
                    return new SolutionEvaluationResult(false, "Runner API communication error", errorResults);
                }
                
                
                if (batchRunnerResult == null)
                {
                     var errorResults = testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, "Failed to deserialize runner batch response.", null, false)).ToList();
                    return new SolutionEvaluationResult(false, "Failed to deserialize runner batch response", errorResults);
                }

                var finalTestCaseResults = new List<TestCaseEvaluationResult>();
                if (batchRunnerResult.CompilationSuccess)
                {
                    
                    foreach (var runnerTcResult in batchRunnerResult.TestCaseResults)
                    {
                        
                        var originalTc = testCases.FirstOrDefault(tc => tc.TestCaseId == runnerTcResult.TestCaseId) ??
                                         testCases.FirstOrDefault(tc => tc.InputFilePath.EndsWith(runnerTcResult.TestCaseId ?? Guid.NewGuid().ToString())); 

                        finalTestCaseResults.Add(new TestCaseEvaluationResult(
                            TestCaseInputPath: originalTc?.InputFilePath ?? runnerTcResult.TestCaseId ?? "Unknown",
                            Status: runnerTcResult.Status, 
                            Stdout: runnerTcResult.Stdout,
                            Stderr: runnerTcResult.Stderr,
                            Message: runnerTcResult.Message,
                            DurationMs: runnerTcResult.DurationMs,
                            MaximumMemoryException: runnerTcResult.MaximumMemoryException
                        ));
                    }
                }
                else 
                {
                    _logger.LogWarning("Compilation failed for batch evaluation. Compiler Output: {CompilerOutput}", batchRunnerResult.CompilerOutput);
                    
                    finalTestCaseResults.AddRange(testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath,
                        EvaluationStatus.CompileError,
                        null, null, 
                        "Compilation failed (see compiler output).",
                        null, false
                    )));
                }

                return new SolutionEvaluationResult(
                    batchRunnerResult.CompilationSuccess,
                    batchRunnerResult.CompilerOutput,
                    finalTestCaseResults
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error during batch solution evaluation. Language: {Language}, CodeFile: {CodeFile}, Container ID (if any): {ContainerId}",
                    language, codeFilePath, containerId ?? "N/A");
                 var errorResults = testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, $"Orchestrator critical error: {ex.Message}", null, false)).ToList();
                 return new SolutionEvaluationResult(false, $"Orchestrator critical error: {ex.Message}", errorResults);
            }
            finally
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
                httpClient?.Dispose();
            }
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
                        new Progress<JSONMessage>(m => {
                             
                             if (m.Status != null && (m.Status.Contains("Pulling fs layer") || m.Status.Contains("Downloading") || m.Status.Contains("Extracting"))) {
                                  
                                  
                             } else if (m.Status != null) {
                                 _logger.LogDebug("Pull status for {Image}: {Status}", imageNameWithTag, m.Status);
                             }
                        })
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
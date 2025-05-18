// Services/DockerCodeExecutionService.cs
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets; // For TcpListener
using System.Net; // For IPAddress
using System.Text; // For Encoding
using System.Text.Json; // For JSON serialization
using System.Net.Http.Headers; // For AuthenticationHeaderValue
using System.Collections.Generic; // For List, Dictionary
using System.Linq;
using WebCodeWorkExecutor.Authentication; // For Linq methods

namespace WebCodeWorkExecutor.Services
{
    // Assuming DockerCodeExecutionService implements ICodeExecutionService
    public class DockerCodeExecutionService : ICodeExecutionService
    {
        private readonly IDockerClient _dockerClient;
        private readonly IHttpClientFactory _httpClientFactory; // Inject HttpClientFactory
        private readonly IConfiguration _configuration; // Inject Configuration
        private readonly ILogger<DockerCodeExecutionService> _logger;
        private readonly string _apiKey; // Store API key securely

        public DockerCodeExecutionService(
            IDockerClient dockerClient,
            IHttpClientFactory httpClientFactory, // Added
            IConfiguration configuration, // Added
            ILogger<DockerCodeExecutionService> logger)
        {
            _dockerClient = dockerClient;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _apiKey = _configuration.GetValue<string>("Authentication:ApiKey") ?? // Get key from config
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
                                  ?? $"generic-runner-{languageLower}:latest"; // Default naming convention

            _logger.LogInformation("Starting batch evaluation for {TestCaseCount} test cases. Language: {Language}, Solution: {CodePath}, Runner Image: {Image}",
                testCases.Count, language, codeFilePath, runnerImageName);

            await EnsureImageExistsAsync(runnerImageName);

            string containerName = $"batch-runner-{languageLower}-{GenerateRandomSuffix(6)}";
            string? containerId = null;
            int hostPort = 0;
            HttpClient? httpClient = null;

            try
            {
                // 1. Get Free Port on Host for the runner container's API
                hostPort = GetFreeTcpPort();
                string internalRunnerPort = "5000";
                string hostPortStr = hostPort.ToString();
                _logger.LogDebug("Obtained free host port {HostPort} for runner's internal port {InternalPort}", hostPort, internalRunnerPort);

                var runnerBatchRequest = new RunnerBatchExecuteRequestDto
                {
                    Language = languageLower,
                    CodeFilePath = codeFilePath, // Path for runner to fetch from Azure/Azurite
                    TestCases = testCases.Select(tc => new RunnerTestCaseItemDto
                    {
                        InputFilePath = tc.InputFilePath,
                        ExpectedOutputFilePath = tc.ExpectedOutputFilePath,
                        TimeLimitMs = tc.MaxExecutionTimeMs,
                        MaxRamMB = tc.MaxRamMB,
                        TestCaseId = tc.TestCaseId // Pass through the identifier
                    }).ToList(),
                };

                // 2. Prepare Environment Variables for Runner Container
                var envVars = new List<string> {
                    $"Authentication__ApiKey={_apiKey}",
                    $"Execution__Language={languageLower}",
                    $"ASPNETCORE_URLS=http://+:{internalRunnerPort}",
                    $"AzureStorage__ConnectionString={_configuration.GetValue<string>("AzureStorage:ConnectionString")}",
                    $"AzureStorage__ContainerName={_configuration.GetValue<string>("AzureStorage:ContainerName")}"
                };

                // 3. Create Container Config
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

                await Task.Delay(TimeSpan.FromSeconds(5)); // Increased delay for potentially larger setup

                httpClient = _httpClientFactory.CreateClient("RunnerApiClient"); // Use named client if configured
                httpClient.BaseAddress = new Uri($"http://localhost:{hostPort}");
                httpClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.ApiKeyHeaderName, _apiKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                _logger.LogInformation("Sending batch evaluation request to runner {ContainerId} via port {HostPort}...", containerId, hostPort);
                // Assume the runner's batch endpoint is "/execute" or "/batch-execute"
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
                    // If the whole batch call fails, all test cases are effectively errored
                    var errorResults = testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, $"Runner API communication failed: {response.StatusCode}", null)).ToList();
                    return new SolutionEvaluationResult(false, "Runner API communication error", errorResults);
                }
                
                // 6. Map Runner's Batch Result to Orchestrator's SolutionEvaluationResult
                if (batchRunnerResult == null)
                {
                     var errorResults = testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, "Failed to deserialize runner batch response.", null)).ToList();
                    return new SolutionEvaluationResult(false, "Failed to deserialize runner batch response", errorResults);
                }

                var finalTestCaseResults = new List<TestCaseEvaluationResult>();
                if (batchRunnerResult.CompilationSuccess)
                {
                    // Map individual test case results
                    foreach (var runnerTcResult in batchRunnerResult.TestCaseResults)
                    {
                        // Find original TestCaseInfo to get InputFilePath for the result (if TestCaseId was used for correlation)
                        var originalTc = testCases.FirstOrDefault(tc => tc.TestCaseId == runnerTcResult.TestCaseId) ??
                                         testCases.FirstOrDefault(tc => tc.InputFilePath.EndsWith(runnerTcResult.TestCaseId ?? Guid.NewGuid().ToString())); // Fallback by trying to match input path part

                        finalTestCaseResults.Add(new TestCaseEvaluationResult(
                            TestCaseInputPath: originalTc?.InputFilePath ?? runnerTcResult.TestCaseId ?? "Unknown",
                            Status: runnerTcResult.Status, // This is now the final verdict from the runner
                            Stdout: runnerTcResult.Stdout,
                            Stderr: runnerTcResult.Stderr,
                            Message: runnerTcResult.Message,
                            DurationMs: runnerTcResult.DurationMs
                        ));
                    }
                }
                else // Compilation failed
                {
                    _logger.LogWarning("Compilation failed for batch evaluation. Compiler Output: {CompilerOutput}", batchRunnerResult.CompilerOutput);
                    // Mark all test cases with compile error
                    finalTestCaseResults.AddRange(testCases.Select(tc => new TestCaseEvaluationResult(
                        tc.InputFilePath,
                        EvaluationStatus.CompileError,
                        null, null, // No stdout/stderr from execution
                        "Compilation failed (see compiler output).",
                        null
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
                        tc.InputFilePath, EvaluationStatus.InternalError, null, null, $"Orchestrator critical error: {ex.Message}", null)).ToList();
                 return new SolutionEvaluationResult(false, $"Orchestrator critical error: {ex.Message}", errorResults);
            }
            finally
            {
                // Stop and (Auto)Remove Container
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
            // Creates a listener on port 0, which asks the OS for a free ephemeral port.
            // Note: There's a small race condition window where the port could be grabbed
            // by another process between stopping the listener and Docker using the port.
            // Usually acceptable for this use case, but not guaranteed unique under high load.
             var listener = new TcpListener(IPAddress.Loopback, 0);
             listener.Start();
             int port = ((IPEndPoint)listener.LocalEndpoint).Port;
             listener.Stop();
             _logger.LogDebug("Found free TCP port: {Port}", port);
             return port;
        }

        // Optional: Helper to ensure image exists locally
        private async Task EnsureImageExistsAsync(string imageNameWithTag)
        {
            if (string.IsNullOrWhiteSpace(imageNameWithTag))
            {
                throw new ArgumentNullException(nameof(imageNameWithTag));
            }

            try
            {
                _logger.LogDebug("Checking if image {Image} exists locally...", imageNameWithTag);

                // --- Corrected Filtering ---
                var imageListParameters = new ImagesListParameters
                {
                    // Use the Filters property to match the reference (name:tag)
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> // Key is "reference"
                        {
                            [imageNameWithTag] = true // Value is dictionary with image name:tag as key
                        }
                    },
                    All = true // Check all images (not just top-level) if needed, usually reference filter is enough
                };

                var imageList = await _dockerClient.Images.ListImagesAsync(imageListParameters);
                // --- End Correction ---

                if (!imageList.Any()) // Check if the filtered list is empty
                {
                    _logger.LogInformation("Image {Image} not found locally. Pulling...", imageNameWithTag);
                    // Extract image name and tag for pull parameters
                    string imageName = imageNameWithTag;
                    string tag = "latest"; // Default tag
                    if (imageNameWithTag.Contains(':'))
                    {
                        var parts = imageNameWithTag.Split(':');
                        imageName = parts[0];
                        tag = parts[1];
                    }
                    else
                    {
                        // If no tag provided, Docker CLI defaults to 'latest', mimic that
                         _logger.LogDebug("No tag specified for {Image}, assuming 'latest'.", imageName);
                    }

                    await _dockerClient.Images.CreateImageAsync(
                        new ImagesCreateParameters { FromImage = imageName, Tag = tag },
                        null, // No auth needed for public images usually
                        new Progress<JSONMessage>(m => {
                             // Avoid logging every single progress message unless needed, maybe log phases
                             if (m.Status != null && (m.Status.Contains("Pulling fs layer") || m.Status.Contains("Downloading") || m.Status.Contains("Extracting"))) {
                                  // More detailed logs if desired during pull
                                  // _logger.LogTrace("Pull progress for {Image}: {Status} {ProgressDetail}", imageNameWithTag, m.Status, m.Progress?.Current);
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
                // Decide if this is critical - maybe runner can't proceed?
                throw; // Re-throw as this might prevent execution
            }
        }
    }
}
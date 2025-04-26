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
            _apiKey = _configuration.GetValue<string>("Authentication:RunnerApiKey") ?? // Get key from config
                      throw new InvalidOperationException("Runner API Key ('Authentication:RunnerApiKey') not configured in Orchestrator.");
        }

        public async Task<List<TestCaseEvaluationResult>> EvaluateSolutionAsync(
            string language,
            string codeFilePath,
            List<TestCaseInfo> testCases,
            ExecutionLimits limits)
        {
            var results = new List<TestCaseEvaluationResult>();
            var languageLower = language.ToLowerInvariant();
            var runnerImage = $"webcodework-container-{languageLower}:latest"; // Determine image name

            _logger.LogInformation("Starting evaluation for {TestCaseCount} test cases. Language: {Language}, Image: {Image}",
                testCases.Count, language, runnerImage);

            // Check if image exists locally (optional optimization)
            // await EnsureImageExistsAsync(runnerImage);

            int testCaseNumber = 0;
            foreach (var testCase in testCases)
            {
                testCaseNumber++;
                _logger.LogInformation("Processing Test Case {Num}: Input='{InputPath}'", testCaseNumber, testCase.InputFilePath);

                string containerName = $"webcodework-runner-{languageLower}-{GenerateRandomSuffix(6)}";
                string? containerId = null;
                int hostPort = 0;
                HttpClient? httpClient = null; // Scope HttpClient within loop/try

                try
                {
                    // 1. Get Free Port on Host (if using port mapping)
                    hostPort = GetFreeTcpPort();
                    string internalPort = "5000"; // Default internal port of runner API
                    string hostPortStr = hostPort.ToString();
                    _logger.LogDebug("Obtained free host port: {HostPort}", hostPort);

                    // 2. Create Container Config
                     var envVars = new List<string> {
                        $"Authentication__ApiKey={_apiKey}", // Pass API Key securely
                        $"Execution__Language={languageLower}",
                        $"ASPNETCORE_URLS=http://+:{internalPort}" // Ensure runner listens correctly
                     };

                    var portBindings = new Dictionary<string, IList<PortBinding>> {
                        { $"{internalPort}/tcp", new List<PortBinding> { new PortBinding { HostPort = hostPortStr } } }
                    };

                    var hostConfig = new HostConfig
                    {
                        PortBindings = portBindings,
                        Memory = limits.MemoryLimitMB * 1024 * 1024, // Convert MB to Bytes
                        // NanoCPUs = ..., // Calculate based on limits if needed
                        AutoRemove = true,
                        NetworkMode = "bridge" // Default bridge network often needed to reach host localhost port
                    };

                    var config = new Config { Image = runnerImage, Env = envVars };

                    // 3. Create & Start Container
                    _logger.LogInformation("Creating container '{ContainerName}' on host port {HostPort}...", containerName, hostPort);
                    var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                        new CreateContainerParameters(config) { HostConfig = hostConfig, Name = containerName }
                    );
                    containerId = createResponse.ID;

                    _logger.LogInformation("Starting container '{ContainerName}' ({ContainerId})...", containerName, containerId);
                    await _dockerClient.Containers.StartContainerAsync(containerId, null);

                    // 4. Wait for Runner API to be ready (simple delay or health check)
                    // IMPORTANT: Add a more robust health check polling http://localhost:{hostPort}/
                    await Task.Delay(TimeSpan.FromSeconds(3)); // Simple delay - REPLACE with proper health check!
                    _logger.LogDebug("Assuming runner API is ready on port {HostPort} for container {ContainerId}", hostPort, containerId);

                    // 5. Prepare Request for Runner API
                    var runnerRequest = new RunnerExecuteRequestDto
                    {
                        Language = languageLower,
                        CodeFilePath = codeFilePath,
                        InputFilePath = testCase.InputFilePath,
                        ExpectedOutputFilePath = testCase.ExpectedOutputFilePath,
                        TimeLimitSeconds = limits.TimeLimitSeconds
                        // MemoryLimitMB = limits.MemoryLimitMB // Pass if runner API uses it
                    };

                    // 6. Call Runner API
                    httpClient = _httpClientFactory.CreateClient(); // Create client
                    httpClient.BaseAddress = new Uri($"http://localhost:{hostPort}"); // Target mapped port
                    httpClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.ApiKeyHeaderName, _apiKey); // Add API Key Header
                    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    _logger.LogInformation("Sending evaluation request to runner container {ContainerId} via port {HostPort}...", containerId, hostPort);
                    HttpResponseMessage response = await httpClient.PostAsJsonAsync("/execute", runnerRequest); // Uses System.Net.Http.Json

                    RunnerExecuteResponseDto? runnerResult = null;
                    if (response.IsSuccessStatusCode)
                    {
                         runnerResult = await response.Content.ReadFromJsonAsync<RunnerExecuteResponseDto>();
                         _logger.LogInformation("Runner API call successful for test case {Num}. Status: {Status}", testCaseNumber, runnerResult?.Status ?? "N/A");
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Runner API call failed for test case {Num}. Status: {StatusCode}, Reason: {Reason}, Content: {Content}",
                            testCaseNumber, response.StatusCode, response.ReasonPhrase, errorContent);
                        // Store internal error for this test case
                         results.Add(new TestCaseEvaluationResult(testCase.InputFilePath, EvaluationStatus.InternalError, null, null, null, $"Runner API failed: {response.StatusCode} - {response.ReasonPhrase}", null));
                         continue; // Move to next test case after logging error
                    }

                    // 7. Map Runner Result to Final Result
                    if(runnerResult != null) {
                         results.Add(new TestCaseEvaluationResult(
                            TestCaseInputPath: testCase.InputFilePath,
                            Status: runnerResult.Status, // Use status from runner
                            CompilerOutput: runnerResult.CompilerOutput,
                            Stdout: runnerResult.Stdout,
                            Stderr: runnerResult.Stderr,
                            Message: runnerResult.Message, // Pass message from runner
                            DurationMs: runnerResult.DurationMs
                        ));
                    } else {
                        // Should not happen if response was success, but handle anyway
                        results.Add(new TestCaseEvaluationResult(testCase.InputFilePath, EvaluationStatus.InternalError, null, null, null, "Failed to deserialize runner response.", null));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Test Case {Num} (Input: {InputPath}). Container ID: {ContainerId}", testCaseNumber, testCase.InputFilePath, containerId ?? "N/A");
                    // Record internal error for this specific test case
                    results.Add(new TestCaseEvaluationResult(testCase.InputFilePath, EvaluationStatus.InternalError, null, null, null, $"Orchestrator error: {ex.Message}", null));
                    // Continue to the next test case
                }
                finally
                {
                    // 8. Stop and Cleanup Container (Stop first, AutoRemove should handle removal)
                    if (!string.IsNullOrEmpty(containerId))
                    {
                        _logger.LogDebug("Stopping container {ContainerId} for test case {Num}...", containerId, testCaseNumber);
                        try
                        {
                            // Give a short grace period before killing
                            await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
                            _logger.LogInformation("Stopped container {ContainerId}.", containerId);
                            // Removal should happen automatically due to AutoRemove=true
                        }
                        catch (DockerContainerNotFoundException) {
                             _logger.LogWarning("Container {ContainerId} already removed or not found during cleanup.", containerId);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.LogError(cleanupEx, "Error stopping/cleaning up container {ContainerId}.", containerId);
                            // Force remove might be needed if stop fails and AutoRemove doesn't work
                             try { await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }); } catch {}
                        }
                    }
                    httpClient?.Dispose(); // Dispose HttpClient
                }
            } // End foreach testCase

            _logger.LogInformation("Finished evaluation for {TestCaseCount} test cases.", testCases.Count);
            return results;
        }


        // --- Helper Methods ---

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
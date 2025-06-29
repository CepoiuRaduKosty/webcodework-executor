namespace WebCodeWorkExecutor.Services
{
    public interface IEvaluationContainerService
    {
        Task<bool> HealthCheck(int port);
        Task<bool> StartEvaluation(string language, string codeFilePath, int submissionId, List<TestCaseInfo> testcases, int port);
    }

    public class EvaluationContainerService : IEvaluationContainerService
    {
        private readonly ILogger<BackendService> _logger;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;

        private readonly string HEALTH_ENDPOINT;

        public EvaluationContainerService(
            ILogger<BackendService> logger,
            IConfiguration config,
            IHttpClientFactory httpClientFactory
        )
        {
            _logger = logger;
            _config = config;
            _httpClientFactory = httpClientFactory;

            HEALTH_ENDPOINT = _config.GetValue<string>("Containers:HealthEndpoint")!;
        }

        private void SetPort(ref HttpClient client, int port)
        {
            var uriBuilder = new UriBuilder(client.BaseAddress!);
            uriBuilder.Port = port;
            client.BaseAddress = uriBuilder.Uri;
        }

        public async Task<bool> HealthCheck(int port)
        {
            var httpClient = _httpClientFactory.CreateClient("ContainersClient");
            SetPort(ref httpClient, port);
            try
            {
                var response = await httpClient.GetAsync(HEALTH_ENDPOINT);
                if (response.IsSuccessStatusCode)
                    return true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> StartEvaluation(string language, string codeFilePath, int submissionId, List<TestCaseInfo> testcases, int port)
        {
            var runnerBatchRequest = new RunnerBatchExecuteRequestDto
            {
                Language = language,
                CodeFilePath = codeFilePath,
                SubmissionId = submissionId,
                TestCases = testcases.Select(tc => new RunnerTestCaseItemDto
                {
                    InputFilePath = tc.InputFilePath,
                    ExpectedOutputFilePath = tc.ExpectedOutputFilePath,
                    TimeLimitMs = tc.MaxExecutionTimeMs,
                    MaxRamMB = tc.MaxRamMB,
                    TestCaseId = tc.TestCaseId
                }).ToList(),
            };

            var httpClient = _httpClientFactory.CreateClient("ContainersClient");
            SetPort(ref httpClient, port);

            var response = await httpClient.PostAsJsonAsync("/execute", runnerBatchRequest);
            if (response.IsSuccessStatusCode)
                return true;
            return false;
        }
    }
}

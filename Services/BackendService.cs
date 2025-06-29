using System.Net.Http.Headers;
using WebCodeWorkExecutor.Dtos;

namespace WebCodeWorkExecutor.Services
{
    public interface IBackendService
    {
        Task SendEvaluationResult(int requestId, OrchestrationEvaluateResponse evalResponse);
    }

    public class BackendService : IBackendService
    {
        private readonly ILogger<BackendService> _logger;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string BASE_API_ENDPOINT;

        public BackendService(
            ILogger<BackendService> logger,
            IConfiguration config,
            IHttpClientFactory httpClientFactory
        )
        {
            _logger = logger;
            _config = config;
            _httpClientFactory = httpClientFactory;

            BASE_API_ENDPOINT = _config.GetValue<string>("Backend:EndpointBase")!;
        }

        public async Task SendEvaluationResult(int submissionId, OrchestrationEvaluateResponse evalResponse)
        {
            var httpClient = _httpClientFactory.CreateClient("BackendClient");
            _logger.LogInformation("Sending batch evaluation response to backend for submission {submissionId}", submissionId);
            await httpClient.PostAsJsonAsync($"{BASE_API_ENDPOINT}/{submissionId}/submit", evalResponse);
        }
    }
}
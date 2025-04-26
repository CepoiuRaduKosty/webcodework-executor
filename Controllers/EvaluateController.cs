// Controllers/EvaluateController.cs (Orchestrator Service)

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebCodeWorkExecutor.Services; // For ICodeExecutionService and internal DTOs
using WebCodeWorkExecutor.Dtos; // For the new Orchestration DTOs
using WebCodeWorkExecutor.Authentication; // For ApiKeyAuthenticationDefaults
using System.Collections.Generic; // For List
using System.Linq; // For Select (mapping)
using System.Threading.Tasks; // For Task

namespace WebCodeWorkExecutor.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationDefaults.AuthenticationScheme)] // Secure controller
    public class EvaluateController : ControllerBase
    {
        private readonly ICodeExecutionService _executionService; // Inject the main service interface
        private readonly ILogger<EvaluateController> _logger;

        // Remove BlobServiceClient/IConfiguration if they are no longer needed directly here
        public EvaluateController(
            ICodeExecutionService executionService,
            ILogger<EvaluateController> logger)
        {
            _executionService = executionService;
            _logger = logger;
        }

        /// <summary>
        /// Orchestrates the evaluation of a code solution against multiple test cases.
        /// Fetches files specified by paths from configured storage, runs code in
        /// isolated containers, and returns results for each test case.
        /// </summary>
        /// <param name="request">The evaluation request details.</param>
        /// <returns>An overall status and detailed results for each test case.</returns>
        [HttpPost("orchestrate")] // Route: POST /api/evaluate/orchestrate
        [ProducesResponseType(typeof(OrchestrationEvaluateResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> OrchestrateEvaluation([FromBody] OrchestrationEvaluateRequest request)
        {
            _logger.LogInformation("Received orchestration request for language: {Language}, CodeFile: {CodeFilePath}, TestCases: {Count}",
                request.Language, request.CodeFilePath, request.TestCases.Count);

            // Basic validation (DTO validation handles required fields and MinLength)
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            // 1. Map Request DTO to Service Input DTOs
            var testCasesInfo = request.TestCases.Select(tc => new Services.TestCaseInfo( // Use the internal service record
                InputFilePath: tc.InputFilePath,
                ExpectedOutputFilePath: tc.ExpectedOutputFilePath
                // Optional: Pass TestCaseId through if needed later for correlation
                // TestCaseName: tc.TestCaseId
            )).ToList();

            var limits = new Services.ExecutionLimits( // Use the internal service record
                TimeLimitSeconds: request.TimeLimitSeconds,
                MemoryLimitMB: request.MemoryLimitMB
            );

            // 2. Call the Execution Service
            List<Services.TestCaseEvaluationResult> serviceResults;
            try
            {
                serviceResults = await _executionService.EvaluateSolutionAsync(
                    request.Language,
                    request.CodeFilePath,
                    testCasesInfo,
                    limits
                );
                 _logger.LogInformation("Execution service returned {Count} results.", serviceResults.Count);
            }
            catch (NotSupportedException langEx) // Catch specific exception if factory throws it
            {
                 _logger.LogWarning("Language not supported during orchestration: {Language}", request.Language);
                 // Return a 400 Bad Request for unsupported language
                 return BadRequest(new OrchestrationEvaluateResponse {
                     OverallStatus = "Failed",
                     Results = request.TestCases.Select(tc => new OrchestrationTestCaseResult {
                         TestCaseInputPath = tc.InputFilePath,
                         TestCaseId = tc.TestCaseId,
                         Status = EvaluationStatus.LanguageNotSupported,
                         Message = langEx.Message
                     }).ToList()
                 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error calling ICodeExecutionService.EvaluateSolutionAsync for language {Language}", request.Language);
                // Return 500 for unexpected errors in the service call
                return StatusCode(StatusCodes.Status500InternalServerError, new OrchestrationEvaluateResponse {
                    OverallStatus = "Failed",
                     Results = request.TestCases.Select(tc => new OrchestrationTestCaseResult {
                         TestCaseInputPath = tc.InputFilePath,
                         TestCaseId = tc.TestCaseId,
                         Status = EvaluationStatus.InternalError,
                         Message = "Orchestrator failed to execute evaluation."
                     }).ToList()
                });
            }

            // 3. Map Service Results to Response DTO
            var response = new OrchestrationEvaluateResponse
            {
                // Determine OverallStatus based on individual results (e.g., "Completed", "PartiallyCompleted")
                OverallStatus = serviceResults.All(r => r.Status == EvaluationStatus.Accepted) ? "Completed" : "CompletedWithIssues", // Example logic
                Results = serviceResults.Select((result, index) => new OrchestrationTestCaseResult
                {
                    // Correlate back using index or TestCaseId if passed through
                    TestCaseInputPath = result.TestCaseInputPath, // Use path from result
                    TestCaseId = request.TestCases.ElementAtOrDefault(index)?.TestCaseId, // Get optional ID back from original request

                    // Map fields directly
                    Status = result.Status,
                    CompilerOutput = result.CompilerOutput,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    Message = result.Message,
                    DurationMs = result.DurationMs
                }).ToList()
            };

            // Determine a more nuanced OverallStatus
            if (response.Results.Any(r => r.Status == EvaluationStatus.InternalError || r.Status == EvaluationStatus.FileError))
                response.OverallStatus = "Failed";
            else if (response.Results.All(r => r.Status == EvaluationStatus.Accepted))
                 response.OverallStatus = "Accepted"; // All passed
            else if (response.Results.Any(r => r.Status != EvaluationStatus.Accepted))
                 response.OverallStatus = "CompletedWithIssues"; // Some passed, some failed


            // 4. Return the results
            return Ok(response);
        }

    }
}

// Controllers/EvaluateController.cs (Orchestrator Service)

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebCodeWorkExecutor.Services;
using WebCodeWorkExecutor.Dtos;
using WebCodeWorkExecutor.Authentication;

namespace WebCodeWorkExecutor.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationDefaults.AuthenticationScheme)]
    public class EvaluateController : ControllerBase
    {
        private readonly ICodeExecutionService _executionService; 
        private readonly ILogger<EvaluateController> _logger;

        public EvaluateController(
            ICodeExecutionService executionService,
            ILogger<EvaluateController> logger)
        {
            _executionService = executionService;
            _logger = logger;
        }

        [HttpPost("orchestrate")] // Route: POST /api/evaluate/orchestrate
        [ProducesResponseType(typeof(OrchestrationEvaluateResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> OrchestrateEvaluation([FromBody] OrchestrationEvaluateRequest request)
        {
            _logger.LogInformation("Received orchestration request for lang: {Lang}, code: {CodeFile}, TCs: {TCCount}",
            request.Language, request.CodeFilePath, request.TestCases.Count);

            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            // 1. Map to internal service DTOs (TestCaseInfo now includes limits)
            var serviceTestCases = request.TestCases.Select(tc => new Services.TestCaseInfo(
                InputFilePath: tc.InputFilePath,
                ExpectedOutputFilePath: tc.ExpectedOutputFilePath,
                MaxExecutionTimeMs: tc.MaxExecutionTimeMs, // Pass per-test-case limit
                MaxRamMB: tc.MaxRamMB,                     // Pass per-test-case limit
                TestCaseId: tc.TestCaseId
            )).ToList();

            // 2. Call the Execution Service
            Services.SolutionEvaluationResult serviceResult;
            try
            {
                serviceResult = await _executionService.EvaluateSolutionAsync(
                    request.Language,
                    request.CodeFilePath,
                    serviceTestCases
                // Global limits for the container itself are now handled internally or via config
                );
            }
            // ... (catch blocks for LanguageNotSupported and general Exception, as before) ...
            catch (NotSupportedException langEx)
            {
                _logger.LogWarning("Language not supported during orchestration: {Language}", request.Language);
                return BadRequest(new OrchestrationEvaluateResponse
                {
                    OverallStatus = "Failed",
                    CompilationSuccess = false,
                    Results = request.TestCases.Select(tc => new OrchestrationTestCaseResult
                    {
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
                return StatusCode(StatusCodes.Status500InternalServerError, new OrchestrationEvaluateResponse
                {
                    OverallStatus = "Failed",
                    CompilationSuccess = false,
                    Results = request.TestCases.Select(tc => new OrchestrationTestCaseResult
                    {
                        TestCaseInputPath = tc.InputFilePath,
                        TestCaseId = tc.TestCaseId,
                        Status = EvaluationStatus.InternalError,
                        Message = "Orchestrator failed to execute evaluation."
                    }).ToList()
                });
            }

            // 3. Map Service Results to API Response DTO
            var response = new OrchestrationEvaluateResponse
            {
                CompilationSuccess = serviceResult.CompilationSuccess,
                CompilerOutput = serviceResult.CompilerOutput,
                Results = serviceResult.TestCaseResults.Select((result, index) => new OrchestrationTestCaseResult
                {
                    TestCaseInputPath = result.TestCaseInputPath,
                    TestCaseId = request.TestCases.ElementAtOrDefault(index)?.TestCaseId, // Correlate back
                    Status = result.Status,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    Message = result.Message,
                    DurationMs = result.DurationMs
                }).ToList()
            };

            // Determine a more nuanced OverallStatus
            if (!response.CompilationSuccess) response.OverallStatus = "CompileError";
            else if (response.Results.Any(r => r.Status != EvaluationStatus.Accepted)) response.OverallStatus = "CompletedWithIssues";
            else if (response.Results.All(r => r.Status == EvaluationStatus.Accepted) && response.Results.Any()) response.OverallStatus = "Accepted";
            else response.OverallStatus = "Completed"; // No test cases or other scenarios

            return Ok(response);
        }

    }
}



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

        [HttpPost("orchestrate")] 
        [ProducesResponseType(typeof(OrchestrationEvaluateResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> OrchestrateEvaluation([FromBody] OrchestrationEvaluateRequest request)
        {
            _logger.LogInformation("Received orchestration request for lang: {Lang}, code: {CodeFile}, TCs: {TCCount}",
            request.Language, request.CodeFilePath, request.TestCases.Count);

            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            
            var serviceTestCases = request.TestCases.Select(tc => new Services.TestCaseInfo(
                InputFilePath: tc.InputFilePath,
                ExpectedOutputFilePath: tc.ExpectedOutputFilePath,
                MaxExecutionTimeMs: tc.MaxExecutionTimeMs, 
                MaxRamMB: tc.MaxRamMB,                     
                TestCaseId: tc.TestCaseId
            )).ToList();

            
            Services.SolutionEvaluationResult serviceResult;
            try
            {
                serviceResult = await _executionService.EvaluateSolutionAsync(
                    request.Language,
                    request.CodeFilePath,
                    serviceTestCases
                
                );
            }
            
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
                        Message = langEx.Message,
                        TestcaseName = tc.InputFilePath,
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

            
            var response = new OrchestrationEvaluateResponse
            {
                CompilationSuccess = serviceResult.CompilationSuccess,
                CompilerOutput = serviceResult.CompilerOutput,
                Results = serviceResult.TestCaseResults.Select((result, index) => new OrchestrationTestCaseResult
                {
                    TestCaseInputPath = result.TestCaseInputPath,
                    TestCaseId = request.TestCases.ElementAtOrDefault(index)?.TestCaseId, 
                    Status = result.Status,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    Message = result.Message,
                    DurationMs = result.DurationMs
                }).ToList()
            };

            
            if (!response.CompilationSuccess) response.OverallStatus = "CompileError";
            else if (response.Results.Any(r => r.Status != EvaluationStatus.Accepted)) response.OverallStatus = "CompletedWithIssues";
            else if (response.Results.All(r => r.Status == EvaluationStatus.Accepted) && response.Results.Any()) response.OverallStatus = "Accepted";
            else response.OverallStatus = "Completed"; 

            return Ok(response);
        }

    }
}

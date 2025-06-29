

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebCodeWorkExecutor.Services;
using WebCodeWorkExecutor.Dtos;

namespace WebCodeWorkExecutor.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EvaluateController : ControllerBase
    {
        private readonly ICodeExecutionService _executionService;
        private readonly ILogger<EvaluateController> _logger;
        private readonly IBackendService _backendService;
        private readonly ContainerJobsTrackerService _containerJobsTrackerService;
        private readonly IConfiguration _config;

        private readonly int MAX_CONCURRENT;

        public EvaluateController(
            ICodeExecutionService executionService,
            ILogger<EvaluateController> logger,
            IBackendService backendService,
            ContainerJobsTrackerService containerJobsTrackerService,
            IConfiguration configuration)
        {
            _executionService = executionService;
            _logger = logger;
            _backendService = backendService;
            _containerJobsTrackerService = containerJobsTrackerService;
            _config = configuration;

            MAX_CONCURRENT = _config.GetValue<int>("GlobalLimits:MaxConcurrentEvaluations");
        }

        private OrchestrationEvaluateResponse BuildExceptionResponse(string message, string language, List<TestCasePathInfo> testcases)
        {
            return new OrchestrationEvaluateResponse
            {
                OverallStatus = "Failed",
                CompilationSuccess = false,
                Language = language,
                Results = testcases.Select(tc => new OrchestrationTestCaseResult
                {
                    TestCaseInputPath = tc.InputFilePath,
                    TestCaseId = tc.TestCaseId,
                    Status = EvaluationStatus.InternalError,
                    Message = message
                }).ToList()
            };
        }

        [HttpPost("orchestrate")]
        [Authorize]
        public async Task<IActionResult> OrchestrateEvaluation([FromBody] OrchestrationEvaluateRequest request)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            _logger.LogInformation("Received orchestration request for lang: {Lang}, code: {CodeFile}, TCs: {TCCount}, submissionId: {SubmissionId}",
            request.Language, request.CodeFilePath, request.TestCases.Count, request.SubmissionId);

            if (_containerJobsTrackerService.GetNumberOfJobs() >= MAX_CONCURRENT)
            {
                _logger.LogInformation("Received evaluation request but job list is full.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    title = "Service Unavailable",
                    status = StatusCodes.Status503ServiceUnavailable,
                    detail = "The job queue is currently full. Please try again later."
                });
            }

            if (_containerJobsTrackerService.IsTracked(request.SubmissionId))
            {
                _logger.LogInformation($"Received duplicate evaluation request for submission {request.SubmissionId}.");
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = $"There is an ongoing evaluation of this submission."
                });
            }

            var serviceTestCases = request.TestCases.Select(tc => new Services.TestCaseInfo(
                InputFilePath: tc.InputFilePath,
                ExpectedOutputFilePath: tc.ExpectedOutputFilePath,
                MaxExecutionTimeMs: tc.MaxExecutionTimeMs,
                MaxRamMB: tc.MaxRamMB,
                TestCaseId: tc.TestCaseId
            )).ToList();

            var jobId = await _executionService.StartCodeEvalAsync(
                request.Language,
                request.CodeFilePath,
                request.SubmissionId,
                serviceTestCases,
                ex =>
                {
                    _logger.LogError(ex, "Unhandled error calling ICodeExecutionService.EvaluateSolutionAsync for language {Language}", request.Language);
                    var exceptionResponse = BuildExceptionResponse("Orchestrator failed to execute evaluation.", request.Language, request.TestCases);
                    _backendService.SendEvaluationResult(request.SubmissionId, exceptionResponse);
                }
            );

            if (jobId is not null) _containerJobsTrackerService.TrackJob(request.SubmissionId, request, jobId, (submissionId, beRequest, _containerId) =>
            {
                _logger.LogError("Evaluation Job Took too long to complete, was forcefully closed.");
                var exceptionResponse = BuildExceptionResponse("Evaluation job took too long to complete.", request.Language, request.TestCases);
                _backendService.SendEvaluationResult(request.SubmissionId, exceptionResponse);
                _executionService.ForceStopEvaluation(jobId);
            });
            return Ok();
        }

        [HttpPost("container-submit")]
        [Authorize(AuthenticationSchemes = "ContainersApiKey")]
        public async Task<IActionResult> ContainerSubmitResult([FromBody] RunnerBatchExecuteResponseDto results)
        {
            _logger.LogInformation("Container sent results back.");

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Container sent results back, but not in the correct format.");
                var errors = ModelState.Where(x => x.Value.Errors.Any())
                               .ToDictionary(
                                   kvp => kvp.Key, // The property name
                                   kvp => kvp.Value.Errors.Select(e => e.ErrorMessage).ToList()
                               );
                foreach (var entry in errors)
                {
                    _logger.LogWarning($"Property '{entry.Key}':");
                    foreach (var errorMessage in entry.Value)
                    {
                        _logger.LogWarning($"- {errorMessage}");
                    }
                }
                return ValidationProblem(ModelState);
            } 

            if (!_containerJobsTrackerService.IsTracked(results.SubmissionId))
            {
                _logger.LogWarning($"Container returned result for untracked submission {results.SubmissionId}.");
                return NotFound("Unknown submission");
            }

            _logger.LogInformation("Runner API batch call successful. Compilation: {CompileStatus}, Test Cases: {Count}",
                results.CompilationSuccess, results.TestCaseResults?.Count ?? 0);

            var jobData = _containerJobsTrackerService.GetJobData(results.SubmissionId)!;
            _containerJobsTrackerService.CompleteSubmission(results.SubmissionId);

            var finalTestCaseResults = new List<TestCaseEvaluationResult>();
            if (results.CompilationSuccess)
            {
                foreach (var runnerTcResult in results.TestCaseResults!)
                {
                    var originalTc = jobData.requestBe.TestCases.FirstOrDefault(tc => tc.TestCaseId == runnerTcResult.TestCaseId) ??
                                        jobData.requestBe.TestCases.FirstOrDefault(tc => tc.InputFilePath.EndsWith(runnerTcResult.TestCaseId ?? Guid.NewGuid().ToString()));

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
                _logger.LogWarning("Compilation failed for batch evaluation. Compiler Output: {CompilerOutput}", results.CompilerOutput);

                finalTestCaseResults.AddRange(jobData.requestBe.TestCases.Select(tc => new TestCaseEvaluationResult(
                    tc.InputFilePath,
                    EvaluationStatus.CompileError,
                    null, null,
                    "Compilation failed (see compiler output).",
                    null, false
                )));
            }

            var evalResult = new SolutionEvaluationResult(
                results.CompilationSuccess,
                results.CompilerOutput,
                finalTestCaseResults
            );

            var response = new OrchestrationEvaluateResponse
            {
                CompilationSuccess = evalResult!.CompilationSuccess,
                CompilerOutput = evalResult.CompilerOutput,
                Language = jobData.requestBe.Language,
                Results = evalResult.TestCaseResults.Select((result, index) => new OrchestrationTestCaseResult
                {
                    TestCaseInputPath = result.TestCaseInputPath,
                    TestCaseId = jobData.requestBe.TestCases.ElementAtOrDefault(index)?.TestCaseId,
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

            _logger.LogInformation($"Sending back to the backend the results for {results.SubmissionId}");
            _ = _backendService.SendEvaluationResult(results.SubmissionId, response);

            return Ok();
        }

    }
}

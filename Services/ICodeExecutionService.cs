// Services/ICodeExecutionService.cs

namespace WebCodeWorkExecutor.Services
{
    // Placeholder DTOs for now
    public record ExecutionRequest(string Code, string Input);
    public record ExecutionResult(bool Success, string? Output, string? Error);

    public interface ICodeExecutionService
    {
        /// <summary>
        /// Evaluates a code solution against multiple test cases using a single runner container.
        /// </summary>
        /// <param name="language">The language (e.g., "c").</param>
        /// <param name="codeFilePath">The path to the solution code file in configured storage.</param>
        /// <param name="testCases">A list containing paths and limits for each test case.</param>
        /// <returns>Overall compilation result and individual results for each test case.</returns>
        Task<SolutionEvaluationResult> EvaluateSolutionAsync(
            string language,
            string codeFilePath,
            List<TestCaseInfo> testCases // TestCaseInfo now includes limits
        );
    }
}
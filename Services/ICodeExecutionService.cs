// Services/ICodeExecutionService.cs

namespace WebCodeWorkExecutor.Services
{
    // Placeholder DTOs for now
    public record ExecutionRequest(string Code, string Input);
    public record ExecutionResult(bool Success, string? Output, string? Error);

    public interface ICodeExecutionService
    {
        /// <summary>
        /// Evaluates a code solution against multiple test cases by spinning up
        /// dedicated runner containers for each test case via Docker.
        /// </summary>
        /// <param name="language">The language (e.g., "c").</param>
        /// <param name="codeFilePath">The path to the solution code file in configured storage.</param>
        /// <param name="testCases">A list containing paths for each test case's input and expected output files.</param>
        /// <param name="limits">Execution limits (time, memory).</param>
        /// <returns>A list of evaluation results, one for each test case.</returns>
        Task<List<TestCaseEvaluationResult>> EvaluateSolutionAsync(
            string language,
            string codeFilePath,
            List<TestCaseInfo> testCases,
            ExecutionLimits limits);
    }
}
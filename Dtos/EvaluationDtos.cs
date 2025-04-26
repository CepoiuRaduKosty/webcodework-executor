// Services/EvaluationDtos.cs (Create or add to this file)
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkExecutor.Services // Or your appropriate namespace
{
    /// <summary>
    /// Information about a single test case file pair in storage.
    /// </summary>
    public record TestCaseInfo(
        string InputFilePath,
        string ExpectedOutputFilePath
        // Optional: Add a name or ID for easier tracking if needed
        // string? TestCaseName
    );

    /// <summary>
    /// Execution limits for the code runner.
    /// </summary>
    public record ExecutionLimits(
        int TimeLimitSeconds = 5,
        int MemoryLimitMB = 256
    );

    /// <summary>
    /// The result of evaluating a single test case.
    /// Mirrors GenericRunnerApi.Dtos.ExecuteResponse structure + input path.
    /// </summary>
    public record TestCaseEvaluationResult(
        string TestCaseInputPath, // Identify which test case this result is for
        string Status, // Final Verdict (Accepted, WrongAnswer, CompileError, etc.)
        string? CompilerOutput,
        string? Stdout,
        string? Stderr,
        string? Message,
        long? DurationMs
        // Add ExitCode etc. if needed
    );

    // --- DTO for calling the Runner API ---
    // (This should match GenericRunnerApi.Dtos.ExecuteRequest)
    internal record RunnerExecuteRequestDto // Internal DTO for clarity
    {
        public string Language { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string CodeFilePath { get; set; } = string.Empty;
        public string InputFilePath { get; set; } = string.Empty;
        public string ExpectedOutputFilePath { get; set; } = string.Empty;
        public int TimeLimitSeconds { get; set; }
        // public int MemoryLimitMB { get; set; }
    }

    // --- DTO for response from the Runner API ---
    // (This should match GenericRunnerApi.Dtos.ExecuteResponse)
    internal record RunnerExecuteResponseDto // Internal DTO for clarity
    {
        public string Status { get; set; } = "INTERNAL_ERROR";
        public string? CompilerOutput { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public string? Message { get; set; }
        public long? DurationMs { get; set; }
    }

    public class EvaluationResponse
    {
        [Required]
        public string Status { get; set; } = EvaluationStatus.InternalError; // Uses the static class

        public string? CompilerOutput { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public string? Message { get; set; }
        public long? DurationMs { get; set; }
    }

    /// <summary>
    /// Defines constant strings for the final evaluation status returned by the orchestrator.
    /// </summary>
    public static class EvaluationStatus // <<< Make sure this class definition exists
    {
        public const string Accepted = "ACCEPTED";
        public const string WrongAnswer = "WRONG_ANSWER";
        public const string CompileError = "COMPILE_ERROR";
        public const string RuntimeError = "RUNTIME_ERROR";
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED";
        public const string MemoryLimitExceeded = "MEMORY_LIMIT_EXCEEDED"; // Keep if planned
        public const string FileError = "FILE_ERROR"; // Error fetching files from storage
        public const string LanguageNotSupported = "LANGUAGE_NOT_SUPPORTED"; // From factory
        public const string InternalError = "INTERNAL_ERROR"; // Orchestrator or runner internal error
    }
}
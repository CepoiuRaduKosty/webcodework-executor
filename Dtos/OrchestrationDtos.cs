// Dtos/OrchestrationDtos.cs
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using WebCodeWorkExecutor.Services; // For List

namespace WebCodeWorkExecutor.Dtos // Or your appropriate DTO namespace
{
    /// <summary>
    /// Represents a single test case file pair provided by the caller.
    /// Paths are expected to be relative to the configured Azure Storage container.
    /// </summary>
    public class TestCasePathInfo
    {
        [Required]
        public string InputFilePath { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutputFilePath { get; set; } = string.Empty;

        // Optional: Identifier for the test case if needed for reporting back
        public string? TestCaseId { get; set; } // e.g., "test1", "edge_case_null"

        [Range(100, 10000)] // Example: 100ms to 10s
        public int MaxExecutionTimeMs { get; set; } = 2000; // Default

        [Range(32, 512)]  // Example: 32MB to 512MB
        public int MaxRamMB { get; set; } = 128;    // Default
    }

    /// <summary>
    /// Request DTO for the main evaluation orchestration endpoint.
    /// </summary>
    public class OrchestrationEvaluateRequest
    {
        [Required]
        public string Language { get; set; } = string.Empty; // "c", "python", etc.
        public string? Version { get; set; } // Optional specific version tag

        [Required]
        public string CodeFilePath { get; set; } = string.Empty; // Path in Azure/Azurite blob

        [Required]
        [MinLength(1, ErrorMessage = "At least one test case must be provided.")]
        public List<TestCasePathInfo> TestCases { get; set; } = new List<TestCasePathInfo>();
    }

    /// <summary>
    /// Represents the evaluation result for a single test case, returned by the orchestrator.
    /// Matches the structure of CodeRunnerService.Services.TestCaseEvaluationResult.
    /// </summary>
    public class OrchestrationTestCaseResult
    {
        public string TestCaseInputPath { get; set; } = string.Empty; // Identifies the test case input
        public string? TestCaseId { get; set; } // Optional ID passed in request

        [Required]
        public string Status { get; set; } = EvaluationStatus.InternalError; // Final Verdict
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public string? Message { get; set; }
        public long? DurationMs { get; set; }
        public string? TestcaseName { get; set; }
    }

    public class OrchestrationEvaluateResponse
    {
        public string OverallStatus { get; set; } = "Error";
        public bool CompilationSuccess { get; set; }
        public string? CompilerOutput { get; set; }
        public List<OrchestrationTestCaseResult> Results { get; set; } = new List<OrchestrationTestCaseResult>();
    }
}

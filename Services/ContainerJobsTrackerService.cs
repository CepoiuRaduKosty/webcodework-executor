using System.Collections.Concurrent;
using WebCodeWorkExecutor.Dtos;

namespace WebCodeWorkExecutor.Services
{
    public record JobData(CancellationTokenSource cts, OrchestrationEvaluateRequest requestBe, string containerId);

    public class ContainerJobsTrackerService
    {
        private readonly ILogger<ContainerJobsTrackerService> _logger;
        private readonly IConfiguration _config;
        private readonly ConcurrentDictionary<int, JobData> _trackedJobs = new();

        private readonly int EXPIRATION_MAX_SECONDS;

        public ContainerJobsTrackerService(IConfiguration config, ILogger<ContainerJobsTrackerService> logger)
        {
            _config = config;
            _logger = logger;

            EXPIRATION_MAX_SECONDS = _config.GetValue<int>("Containers:MaxTimeSecondsPerEvaluation");
        }

        public void TrackJob(int submissionId, OrchestrationEvaluateRequest sourceRequest, string containerId, Action<int, OrchestrationEvaluateRequest, string> onTimeout)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(EXPIRATION_MAX_SECONDS));
            cts.Token.Register(() =>
            {
                onTimeout(submissionId, sourceRequest, containerId);
                if (_trackedJobs.TryRemove(submissionId, out var _jobData))
                {
                    _jobData.cts.Dispose();
                }
            });
            if (!_trackedJobs.TryAdd(submissionId, new JobData(cts, sourceRequest, containerId)))
            {
                cts.Dispose();
                _logger.LogInformation("Attempted to track submission {SubmissionId} which is already being tracked.", submissionId);
            }
            else
            {
                _logger.LogInformation("Started tracking submission {SubmissionId} with a timeout of {Timeout} s.", submissionId, EXPIRATION_MAX_SECONDS);
            }
        }

        public void CompleteSubmission(int submissionId)
        {
            if (_trackedJobs.TryRemove(submissionId, out var jobData))
            {
                _logger.LogInformation("Completing tracking for submission {SubmissionId}.", submissionId);
                jobData.cts.Dispose();
            }
        }

        public bool IsTracked(int submissionId)
        {
            return _trackedJobs.ContainsKey(submissionId);
        }

        public JobData? GetJobData(int submissionId)
        {
            JobData? data;
            var result = _trackedJobs.TryGetValue(submissionId, out data);
            if (result)
                return data;
            return null;
        }

        public int GetNumberOfJobs()
        {
            return _trackedJobs.Count();
        }
    }
}
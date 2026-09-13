using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using LogAnalyzer;
using LogAnalyzerRpc.Protos;
using LogAnalyzerRpc;
using LogParser.Visitors;

namespace LogAnalyzerAgent.Applications
{
    public class AgentSession
    {
        private readonly LogFileAnalyzer _analyzer;
        private readonly ILogger _logger;

        public AgentSession(LogFileAnalyzer analyzer, ILoggerFactory loggerFactory)
        {
            _analyzer = analyzer;
            _logger = loggerFactory.CreateLogger<AgentSession>();
        }

        private static OperationStatusMessage CreateInternalErrorOperationStatus(Exception ex)
        {
            return new OperationStatusMessage()
            {
                Success = false,
                Code = AgentErrorCode.InternalError,
                Message = $"An error occurred while retrieving agent status: {ex.Message}",
            };
        }

        private static OperationStatusMessage CreateNoErrorOperationStatus()
        {
            return new OperationStatusMessage()
            {
                Success = true,
                Code = AgentErrorCode.NoAgentError,
                Message = "",
            };
        }

        private static OperationStatusMessage CreateErrorOperationStatus(AgentErrorCode code, string message)
        {
            return new OperationStatusMessage
            {
                Success = false,
                Code = code,
                Message = message,
            };
        }

        public Task<Empty> Ping(Empty empty, CancellationToken cancellationToken)
        {
            return Task.FromResult(new Empty());
        }

        public Task<GetAgentStatusResponse> GetAgentStatus(Empty empty, CancellationToken cancellationToken)
        {
            var response = new GetAgentStatusResponse();
            try
            {
                response.HasDirectory = _analyzer.HasDirectory;
                response.CurrentDirectory = _analyzer.CurrentDirectory ?? "";
                response.IsAnalyzing = _analyzer.IsAnalyzing;
                response.Status = CreateNoErrorOperationStatus();
            }
            catch (Exception ex)
            {
                response.Status = CreateInternalErrorOperationStatus(ex);
                _logger.LogError(ex, "An error occurred while retrieving agent status.");
            }
            return Task.FromResult(response);
        }

        public Task<GetLogFilesResponse> GetLogFiles(Empty empty, CancellationToken cancellationToken)
        {
            var response = new GetLogFilesResponse();
            try
            {
                response.FileNames.AddRange(_analyzer.GetLogFiles());
                response.Status = CreateNoErrorOperationStatus();
            }
            catch (Exception ex)
            {
                response.Status = CreateInternalErrorOperationStatus(ex);
                _logger.LogError(ex, "An error occurred while retrieving log files.");
            }
            return Task.FromResult(response);
        }

        public Task<ChangeDirectoryResponse> ChangeDirectory(ChangeDirectoryRequest request, CancellationToken cancellationToken)
        {
            var response = new ChangeDirectoryResponse();
            try
            {
                if (request is null)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, "Request cannot be null.");
                    return Task.FromResult(response);
                }

                if (_analyzer.IsAnalyzing)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "Cannot change directory while analysis is in progress.");
                    return Task.FromResult(response);
                }

                if (!string.IsNullOrEmpty(request.DirectoryPath) && !Directory.Exists(request.DirectoryPath))
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.DirectoryNotFound, $"Directory not found: {request.DirectoryPath}");
                    return Task.FromResult(response);
                }

                if (!_analyzer.ChangeDirectory(request.DirectoryPath))
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "The directory cannot be changed at this time.");
                    return Task.FromResult(response);
                }

                response.CurrentDirectory = _analyzer.CurrentDirectory ?? "";
                response.FileNames.AddRange(_analyzer.GetLogFiles());
                response.Status = CreateNoErrorOperationStatus();
            }
            catch (ArgumentException ex)
            {
                response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, ex.Message);
            }
            catch (Exception ex)
            {
                response.Status = CreateInternalErrorOperationStatus(ex);
                _logger.LogError(ex, "An error occurred while changing directory.");
            }
            return Task.FromResult(response);
        }

        public Task<AnalyzeAllResponse> AnalyzeAll(AnalyzeAllRequest request, CancellationToken cancellationToken)
        {
            var response = new AnalyzeAllResponse();
            try
            {
                if (request is null || request.DegreeOfParallelism < 0)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, "Degree of parallelism must be non-negative.");
                    return Task.FromResult(response);
                }

                if (!_analyzer.HasDirectory)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "No log directory has been selected.");
                    return Task.FromResult(response);
                }

                if (_analyzer.IsAnalyzing)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "Analysis is already in progress.");
                    return Task.FromResult(response);
                }

                _analyzer.AnalyzeAll(request.DegreeOfParallelism);
                response.Status = CreateNoErrorOperationStatus();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, ex.Message);
            }
            catch (Exception ex)
            {
                response.Status = CreateInternalErrorOperationStatus(ex);
                _logger.LogError(ex, "An error occurred while analyzing all files.");
            }
            return Task.FromResult(response);
        }

        public Task<AnalyzeFilesResponse> AnalyzeFiles(AnalyzeFilesRequest request, CancellationToken cancellationToken)
        {
            var response = new AnalyzeFilesResponse();
            try
            {
                if (request is null || request.DegreeOfParallelism < 0)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, "Degree of parallelism must be non-negative.");
                    return Task.FromResult(response);
                }

                if (!_analyzer.HasDirectory)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "No log directory has been selected.");
                    return Task.FromResult(response);
                }

                var fileNames = request.FileNames.ToList();
                var knownFiles = _analyzer.GetLogFiles().ToHashSet(StringComparer.Ordinal);
                var missingFile = fileNames.FirstOrDefault(fileName => !knownFiles.Contains(fileName));
                if (missingFile is not null)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.FileNotFound, $"File not found: {missingFile}");
                    return Task.FromResult(response);
                }

                if (_analyzer.IsAnalyzing)
                {
                    response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, "Analysis is already in progress.");
                    return Task.FromResult(response);
                }

                _analyzer.AnalyzeFiles(request.DegreeOfParallelism, fileNames);
                response.Status = CreateNoErrorOperationStatus();
            }
            catch (ArgumentException ex)
            {
                response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                response.Status = CreateErrorOperationStatus(AgentErrorCode.InvalidOperation, ex.Message);
            }
            catch (Exception ex)
            {
                response.Status = CreateInternalErrorOperationStatus(ex);
                _logger.LogError(ex, "An error occurred while analyzing selected files.");
            }
            return Task.FromResult(response);
        }

        public IReadOnlyList<GetAnalysisResultResponse> GetAnalysisResult(GetAnalysisResultRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (request is null || string.IsNullOrWhiteSpace(request.FileName))
                {
                    return new[]
                    {
                        new GetAnalysisResultResponse
                        {
                            Status = CreateErrorOperationStatus(AgentErrorCode.InvalidArgument, "File name cannot be empty.")
                        }
                    };
                }

                if (!_analyzer.TryGetAnalysisResult(request.FileName, out var result) || result is null)
                {
                    return new[]
                    {
                        new GetAnalysisResultResponse
                        {
                            Status = CreateErrorOperationStatus(AgentErrorCode.FileNotFound, $"File not found: {request.FileName}")
                        }
                    };
                }

                var header = new AnalysisResultHeaderMessage
                {
                    FileName = result.FileName,
                    FullName = result.FullName,
                    State = GrpcTypeConverter.ConvertToGrpc(result.State),
                    WorkerId = result.WorkerId,
                };
                if (result.ErrorMessage is not null)
                {
                    header.ErrorMessage = result.ErrorMessage;
                }

                var responses = new List<GetAnalysisResultResponse>
                {
                    new()
                    {
                        Header = header,
                        Status = CreateNoErrorOperationStatus(),
                    }
                };

                if (result.State == AnalysisState.Succeeded)
                {
                    responses.AddRange(result.Entries.Select(entry => new GetAnalysisResultResponse
                    {
                        LogEntry = GrpcTypeConverter.ConvertToGrpc(entry),
                        Status = CreateNoErrorOperationStatus(),
                    }));
                }

                return responses;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while retrieving analysis result.");
                return new[]
                {
                    new GetAnalysisResultResponse
                    {
                        Status = CreateInternalErrorOperationStatus(ex)
                    }
                };
            }
        }
    }
}

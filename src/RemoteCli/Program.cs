using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using LogAnalyzerRpc;
using LogAnalyzerRpc.Protos;
using LogParser.Visitors;
using Microsoft.Extensions.Logging;

namespace RemoteCli
{
    using LogAnalyzerAgentServiceClient = LogAnalyzerAgentService.LogAnalyzerAgentServiceClient;

    public class Program
    {
        static async Task Main(string[] args)
        {
            var address = args.FirstOrDefault()
                ?? Environment.GetEnvironmentVariable("LOG_ANALYZER_AGENT_ADDRESS")
                ?? "http://localhost:5000";
            Console.WriteLine($"Connecting to agent at {address}...");
            using var channel = GrpcChannel.ForAddress(address);
            var client = new LogAnalyzerAgentServiceClient(channel);
            try
            {
                _ = await client.PingAsync(new Empty());
                await ChooseAction(client);
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"Unable to communicate with agent: {ex.Status.Detail}");
            }
        }

        private static async Task<bool> InputDirectory(LogAnalyzerAgentServiceClient client)
        {
            while (true)
            {
                Console.WriteLine("Please input directory containing log files:");
                var directory = Console.ReadLine();
                if (directory is null)
                {
                    return false;
                }
                var request = new ChangeDirectoryRequest()
                {
                    DirectoryPath = directory,
                };
                var response = await client.ChangeDirectoryAsync(request);
                if (!response.Status.Success)
                {
                    Console.WriteLine($"Error: {response.Status.Code}: {response.Status.Message}, please try again:");
                    continue;
                }
                break;
            }
            return true;
        }

        private static async Task ChooseAction(LogAnalyzerAgentServiceClient client)
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("""
                Please choose:
                1. Show log files.
                2. Analyze specified log files.
                3. Analyze all log files.
                4. Get log file analysis result.
                5. Change directory.
                6. Exit.
                """);
                Console.Write(">>> ");
                Console.Out.Flush();

                int choice = 0;
                var choiceStr = Console.ReadLine();
                if (choiceStr is null)
                {
                    return;
                }
                try
                {
                    choice = int.Parse(choiceStr);
                }
                catch (Exception)
                {
                    Console.WriteLine("Invalid input, please try again.");
                    continue;
                }

                var actions = new Dictionary<int, Func<LogAnalyzerAgentServiceClient, Task>>
                {
                    { 1, ShowLogFiles },
                    { 2, AnalyzeFiles },
                    { 3, AnalyzeAll },
                    { 4, GetAnalysisResult }
                };
                switch (choice)
                {
                    case 1:
                    case 2:
                    case 3:
                    case 4:
                        await actions[choice](client);
                        break;
                    case 5:
                        var success = await InputDirectory(client);
                        if (!success)
                        {
                            return;
                        }
                        break;
                    case 6:
                        return;
                    default:
                        Console.WriteLine("Invalid choice, please try again.");
                        break;
                }
            }
        }

        private static async Task ShowLogFiles(LogAnalyzerAgentServiceClient client)
        {
            try
            {
                var response = await client.GetLogFilesAsync(new Empty());
                if (!response.Status.Success)
                {
                    Console.WriteLine($"Error: {response.Status.Code}: {response.Status.Message}");
                    return;
                }

                if (response.FileNames.Count == 0)
                {
                    Console.WriteLine("No .log files found.");
                    return;
                }

                Console.WriteLine("Log files:");
                foreach (var fileName in response.FileNames)
                {
                    Console.WriteLine($"- {fileName}");
                }
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"Unable to retrieve log files: {ex.Status.Detail}");
            }
        }

        private static int ReadDegreeOfParallelism()
        {
            Console.Write("Degree of parallelism (0 = automatic): ");
            var input = Console.ReadLine();
            if (!int.TryParse(input, out var degree) || degree < 0)
            {
                throw new ArgumentException("Degree of parallelism must be a non-negative integer.");
            }
            return degree;
        }

        private static List<string> ReadFileNames()
        {
            Console.Write("Comma-separated file names: ");
            var input = Console.ReadLine();
            if (input is null)
            {
                return new List<string>();
            }

            return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        private static async Task AnalyzeFiles(LogAnalyzerAgentServiceClient client)
        {
            try
            {
                var request = new AnalyzeFilesRequest
                {
                    DegreeOfParallelism = ReadDegreeOfParallelism(),
                };
                request.FileNames.AddRange(ReadFileNames());
                var response = await client.AnalyzeFilesAsync(request);
                Console.WriteLine(response.Status.Success
                    ? "Analysis completed."
                    : $"Error: {response.Status.Code}: {response.Status.Message}");
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"Invalid input: {ex.Message}");
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"Unable to analyze files: {ex.Status.Detail}");
            }
        }

        private static async Task AnalyzeAll(LogAnalyzerAgentServiceClient client)
        {
            try
            {
                var response = await client.AnalyzeAllAsync(new AnalyzeAllRequest
                {
                    DegreeOfParallelism = ReadDegreeOfParallelism(),
                });
                Console.WriteLine(response.Status.Success
                    ? "Analysis completed."
                    : $"Error: {response.Status.Code}: {response.Status.Message}");
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"Invalid input: {ex.Message}");
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"Unable to analyze files: {ex.Status.Detail}");
            }
        }

        private static async Task GetAnalysisResult(LogAnalyzerAgentServiceClient client)
        {
            Console.Write("File name: ");
            var fileName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(fileName))
            {
                Console.WriteLine("File name cannot be empty.");
                return;
            }

            try
            {
                using var call = client.GetAnalysisResult(new GetAnalysisResultRequest { FileName = fileName });
                var visitor = new KeyValueVisitor();
                await foreach (var response in call.ResponseStream.ReadAllAsync())
                {
                    if (!response.Status.Success)
                    {
                        Console.WriteLine($"Error: {response.Status.Code}: {response.Status.Message}");
                        continue;
                    }

                    if (response.PayloadCase == GetAnalysisResultResponse.PayloadOneofCase.Header)
                    {
                        var header = response.Header;
                        Console.WriteLine($"{header.FileName}: {header.State}");
                        if (header.State == AnalysisStateEnum.Failed)
                        {
                            Console.WriteLine($"Error: {header.ErrorMessage}");
                        }
                    }
                    else if (response.PayloadCase == GetAnalysisResultResponse.PayloadOneofCase.LogEntry)
                    {
                        var values = visitor.Dump(GrpcTypeConverter.ConvertFromGrpc(response.LogEntry));
                        Console.WriteLine(string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}")));
                    }
                }
            }
            catch (RpcException ex)
            {
                Console.WriteLine($"Unable to retrieve analysis result: {ex.Status.Detail}");
            }
        }
    }
}

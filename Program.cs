using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

using Grpc.Core;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace Microsoft.Azure.Functions.PowerShellWorker
{
    public class Program
    {
        private static string s_host;
        private static int s_port;
        private static string s_workerId;
        private static string s_requestId;
        private static FunctionRpc.FunctionRpcClient s_client;
        private static AsyncDuplexStreamingCall<StreamingMessage, StreamingMessage> s_call;
        private static System.Management.Automation.PowerShell s_ps;
        private static Dictionary<string, RpcFunctionMetadata> s_loadedFunctions =
            new Dictionary<string, RpcFunctionMetadata>(StringComparer.OrdinalIgnoreCase);

        public static void Main(string[] args)
        {
            if (args.Length != 5)
            {
                Console.WriteLine("usage <hostName> <portNumber> <workerId> <requestId> <grpcMaxMessageLength>");
                return;
            }

            int grpcMaxMessageLength;
            for (int i = 0; i < 5; i++)
            {
                string currentArg = args[i];
                switch (i)
                {
                    case 0: s_host = currentArg; break;
                    case 1: s_port = int.Parse(currentArg); break;
                    case 2: s_workerId = currentArg; break;
                    case 3: s_requestId = currentArg; break;
                    case 4: grpcMaxMessageLength = int.Parse(currentArg); break;
                    default: throw new InvalidOperationException();
                }
            }

            Channel channel = new Channel(s_host, s_port, ChannelCredentials.Insecure);
            s_client = new FunctionRpc.FunctionRpcClient(channel);
            s_call = s_client.EventStream();
            s_ps = System.Management.Automation.PowerShell.Create(InitialSessionState.CreateDefault2());

            var streamingMessage = new StreamingMessage() {
                RequestId = s_requestId,
                StartStream = new StartStream() { WorkerId = s_workerId }
            };
            s_call.RequestStream.WriteAsync(streamingMessage);

            ProcessEvent().Wait();
        }

        private static async Task ProcessEvent()
        {
            using (s_call)
            {
                while (await s_call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    var message = s_call.ResponseStream.Current;
                    switch (message.ContentCase)
                    {
                        case StreamingMessage.ContentOneofCase.WorkerInitRequest:
                            await HandleWorkerInitRequest(message.WorkerInitRequest);
                            break;

                        case StreamingMessage.ContentOneofCase.FunctionLoadRequest:
                            await HandleFunctionLoadRequest(message.FunctionLoadRequest);
                            break;

                        case StreamingMessage.ContentOneofCase.InvocationRequest:
                            await HandleInvocationRequest(message.InvocationRequest);
                            break;

                        default:
                            throw new InvalidOperationException($"Not supportted message type: {message.ContentCase}");
                    }
                }
            }
        }

        private static async Task HandleWorkerInitRequest(WorkerInitRequest initRequest)
        {
            var response = new StreamingMessage()
            {
                RequestId = s_requestId,
                WorkerInitResponse = new WorkerInitResponse()
                {
                    Result = new StatusResult()
                    {
                        Status = StatusResult.Types.Status.Success
                    }
                }
            };
            await s_call.RequestStream.WriteAsync(response);
        }

        private static async Task HandleFunctionLoadRequest(FunctionLoadRequest loadRequest)
        {
            s_loadedFunctions.TryAdd(loadRequest.FunctionId, loadRequest.Metadata);
            var response = new StreamingMessage()
            {
                RequestId = s_requestId,
                FunctionLoadResponse = new FunctionLoadResponse()
                {
                    FunctionId = loadRequest.FunctionId,
                    Result = new StatusResult()
                    {
                        Status = StatusResult.Types.Status.Success
                    }
                }
            };
            await s_call.RequestStream.WriteAsync(response);
        }

        private static async Task HandleInvocationRequest(InvocationRequest invokeRequest)
        {
            var status = new StatusResult() { Status = StatusResult.Types.Status.Success };
            var response = new StreamingMessage()
            {
                RequestId = s_requestId,
                InvocationResponse = new InvocationResponse()
                {
                    InvocationId = invokeRequest.InvocationId,
                    Result = status
                }
            };

            var metadata = s_loadedFunctions[invokeRequest.FunctionId];
            
            // Not exactly sure what to do with bindings yet, so only handles 'httpTrigger-in' + 'http-out'
            string outHttpName = null;
            foreach (var binding in metadata.Bindings)
            {
                if (binding.Value.Direction == BindingInfo.Types.Direction.In)
                {
                    continue;
                }

                if (binding.Value.Type == "http")
                {
                    outHttpName = binding.Key;
                    break;
                }
            }

            if (outHttpName == null)
            {
                status.Status = StatusResult.Types.Status.Failure;
                status.Result = "PowerShell worker only handles http out binding for now.";
            }
            else
            {
                object argument = null;
                foreach (var input in invokeRequest.InputData)
                {
                    if (input.Data != null && input.Data.Http != null)
                    {
                        argument = input.Data.Http.Params;
                    }
                }

                s_ps.AddCommand(metadata.ScriptFile);
                if (argument != null)
                {
                    s_ps.AddArgument(argument);
                }

                TypedData retValue;
                try
                {
                    var results = s_ps.Invoke<string>();
                    retValue = new TypedData() { String = String.Join(',', results) };
                }
                finally
                {
                    s_ps.Commands.Clear();
                }

                // This is just mimic what nodejs worker does
                var paramBinding = new ParameterBinding()
                {
                    Name = outHttpName,
                    Data = new TypedData()
                    {
                        Http = new RpcHttp()
                        {
                            StatusCode = "200",
                            Body = retValue
                        }
                    }
                };

                // Not exactly sure which one to use for what scenario, so just set both.
                response.InvocationResponse.OutputData.Add(paramBinding);
                response.InvocationResponse.ReturnValue = retValue;
            }

            await s_call.RequestStream.WriteAsync(response);
        }
    }
}

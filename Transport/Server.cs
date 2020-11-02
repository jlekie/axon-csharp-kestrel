using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using Microsoft.Extensions.Configuration;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Adapter.Internal;

namespace Axon.Kestrel.Transport
{
    public class AxonStartup
    {
        public IConfiguration Configuration { get; }

        public AxonStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
        }
    }

    public class AxonKestrelHostingOptions
    {
        public string Route { get; set; } = "/axon";
        public int RequestTimeout { get; set; } = 30000;
    }
    public static class KestrelExtensions
    {
        public static IApplicationBuilder UseAxon(this IApplicationBuilder app, InprocClientTransport client, Action<AxonKestrelHostingOptions> configure)
        {
            var options = new AxonKestrelHostingOptions();
            configure(options);

            app.Map(options.Route, axonApp =>
            {
                axonApp.MapWhen(context => context.Request.Path.StartsWithSegments("/req") && context.Request.Method == "POST", requestApp =>
                {
                    requestApp.Run(async context =>
                    {
                        if (!context.Request.Query.TryGetValue("tag", out var tag))
                            throw new Exception("Tag required");

                        var cancellationSource = new CancellationTokenSource(options.RequestTimeout);

                        TransportMessage message;
                        using (var stream = new MemoryStream())
                        {
                            await context.Request.Body.CopyToAsync(stream);
                            stream.Position = 0;

                            using (var reader = new BinaryReader(stream))
                                message = reader.ReadTransportMessage();
                        }

                        await client.Send(tag, message, cancellationSource.Token);

                        var responseMessage = await client.Receive(tag, cancellationSource.Token);
                        using (var writer = new BinaryWriter(context.Response.Body))
                            writer.WriteTransportMessage(responseMessage);
                    });
                });

                axonApp.MapWhen(context => context.Request.Path.StartsWithSegments("/send") && context.Request.Method == "POST", requestApp =>
                {
                    requestApp.Run(async context =>
                    {
                        var cancellationSource = new CancellationTokenSource(options.RequestTimeout);

                        TransportMessage message;
                        using (var stream = new MemoryStream())
                        {
                            await context.Request.Body.CopyToAsync(stream);
                            stream.Position = 0;

                            using (var reader = new BinaryReader(stream))
                                message = reader.ReadTransportMessage();
                        }

                        if (context.Request.Query.TryGetValue("tag", out var tag))
                            await client.Send(tag, message, cancellationSource.Token);
                        else
                            await client.Send(message, cancellationSource.Token);
                    });
                });

                axonApp.MapWhen(context => context.Request.Path.StartsWithSegments("/receive") && context.Request.Method == "GET", requestApp =>
                {
                    requestApp.Run(async context =>
                    {
                        var cancellationSource = new CancellationTokenSource(options.RequestTimeout);

                        TransportMessage message;
                        if (context.Request.Query.TryGetValue("tag", out var tag))
                            message = await client.Receive(tag, cancellationSource.Token);
                        else
                            message = await client.Receive(cancellationSource.Token);

                        using (var writer = new BinaryWriter(context.Response.Body))
                            writer.WriteTransportMessage(message);
                    });
                });
            });

            return app;
        }
    }

    public class TransportForwarder
    {
        public event EventHandler<DiagnosticMessageEventArgs> DiagnosticMessage;

        public InprocServerTransport Transport { get; }
        private ConcurrentDictionary<string, BlockingCollection<IClientTransport>> Backends { get; } = new ConcurrentDictionary<string, BlockingCollection<IClientTransport>>();

        public TransportForwarder()
        {
            this.Transport = new InprocServerTransport();
            this.Transport.ReceiveStream.MessageEnqueued += this.FrontendTransportMessageEnqueued;
        }
        public TransportForwarder(InprocServerTransport transport)
        {
            this.Transport = transport;
            this.Transport.ReceiveStream.MessageEnqueued += this.FrontendTransportMessageEnqueued;
        }

        public void AddBackend(string identifier, IClientTransport backend)
        {
            this.AddBackend(identifier, backend, CancellationToken.None);
        }
        public void AddBackend(string identifier, IClientTransport backend, CancellationToken cancellationToken)
        {
            this.Backends.GetOrAdd(identifier, (_) => new BlockingCollection<IClientTransport>()).Add(backend, cancellationToken);

            backend.MessageReceiving += this.BackendMessageReceived;
        }

        private async void FrontendTransportMessageEnqueued(object sender, EventArgs e)
        {
            var message = await this.Transport.Receive();
            string serviceIdentifier = message.Metadata.TryGetLast("serviceIdentifier", out var encodedService) ? Encoding.UTF8.GetString(encodedService) : throw new Exception();

            try
            {
                var cancellationSource = new CancellationTokenSource(5000);
                var registeredBackend = this.ResolveBackend(serviceIdentifier, cancellationSource.Token);

                await registeredBackend.Send(message);
            }
            catch (OperationCanceledException)
            {
                this.OnDiagnosticMessage($"No backends available for {serviceIdentifier}!!!");
            }
        }
        private async void BackendMessageReceived(object sender, MessagingEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(e.Tag))
                    await this.Transport.Send(e.Message);
                else
                    await this.Transport.Send(e.Tag, e.Message);
            }
            catch (Exception ex)
            {
                this.OnDiagnosticMessage($"Transport forwarder messaging error: " + ex.Message);
            }
        }

        private IClientTransport ResolveBackend(string identifier, CancellationToken cancellationToken)
        {
            var backends = this.Backends.GetOrAdd(identifier, (_) => new BlockingCollection<IClientTransport>());

            var backend = backends.Take(cancellationToken);
            while (!backend.IsConnected)
            {
                Console.WriteLine($"Client backend expired [{identifier}/{backend.Identity}]");
                backend = backends.Take(cancellationToken);
            }

            backends.Add(backend);

            return backend;
        }

        protected virtual void OnDiagnosticMessage(string message)
        {
            this.DiagnosticMessage?.Invoke(this, new DiagnosticMessageEventArgs(message));
        }
    }

    public class HttpClientClientTransport : AClientTransport
    {
        public override bool IsConnected => true;

        public HttpClient HttpClient { get; }

        private ConcurrentDictionary<string, BlockingCollection<Task<TransportMessage>>> PendingRequests { get; } = new ConcurrentDictionary<string, BlockingCollection<Task<TransportMessage>>>();

        public HttpClientClientTransport(HttpClient httpClient)
        {
            this.HttpClient = httpClient;
        }

        public override Task Connect()
        {
            return Task.CompletedTask;
        }
        public override Task Connect(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task Close()
        {
            return Task.CompletedTask;
        }
        public override Task Close(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task<TransportMessage> Receive()
        {
            return this.Receive(CancellationToken.None);
        }
        public override async Task<TransportMessage> Receive(CancellationToken cancellationToken)
        {
            var reqMessage = new HttpRequestMessage(HttpMethod.Get, "axon/receive")
            {
                Version = new Version(2, 0)
            };
            var response = await this.HttpClient.SendAsync(reqMessage);

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new BinaryReader(stream))
                return reader.ReadTransportMessage();
        }
        public override Task<TransportMessage> Receive(string messageId)
        {
            return this.Receive(messageId, CancellationToken.None);
        }
        public override Task<TransportMessage> Receive(string messageId, CancellationToken cancellationToken)
        {
            //var reqMessage = new HttpRequestMessage(HttpMethod.Get, $"axon/receive?tag={messageId}")
            //{
            //    Version = new Version(2, 0)
            //};
            //var response = await this.HttpClient.SendAsync(reqMessage);

            //using (var stream = await response.Content.ReadAsStreamAsync())
            //using (var reader = new BinaryReader(stream))
            //    return reader.ReadTransportMessage();

            return this.PendingRequests.GetOrAdd(messageId, (_) => new BlockingCollection<Task<TransportMessage>>()).Take(cancellationToken);
        }

        public override Task<TaggedTransportMessage> ReceiveTagged()
        {
            throw new NotImplementedException();
        }

        public override Task<TaggedTransportMessage> ReceiveTagged(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task Send(TransportMessage message)
        {
            return this.Send(message, CancellationToken.None);
        }
        public override async Task Send(TransportMessage message, CancellationToken cancellationToken)
        {
            using (var stream = new MemoryStream())
            using (var content = new StreamContent(stream))
            using (var writer = new BinaryWriter(stream))
            {
                writer.WriteTransportMessage(message);
                
                var reqMessage = new HttpRequestMessage(HttpMethod.Post, "axon/send")
                {
                    Content = content,
                    Version = new Version(2, 0)
                };

                await this.HttpClient.SendAsync(reqMessage, cancellationToken);
            }
        }

        public override Task Send(string messageId, TransportMessage message)
        {
            return this.Send(messageId, message, CancellationToken.None);
        }
        public override async Task Send(string messageId, TransportMessage message, CancellationToken cancellationToken)
        {
            byte[] data;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.WriteTransportMessage(message);

                data = stream.ToArray();
            }

            var reqMessage = new HttpRequestMessage(HttpMethod.Post, $"axon/req?tag={messageId}")
            {
                Content = new ByteArrayContent(data),
                Version = new Version(2, 0)
            };

            //await this.HttpClient.SendAsync(reqMessage, cancellationToken);
            this.PendingRequests.GetOrAdd(messageId, (_) => new BlockingCollection<Task<TransportMessage>>()).Add(this.HttpClient.SendAsync(reqMessage, cancellationToken).ContinueWith(async sendTask =>
            {
                using (var stream = await sendTask.Result.Content.ReadAsStreamAsync())
                using (var reader = new BinaryReader(stream))
                    return reader.ReadTransportMessage();
            }).Unwrap());
        }

        public override Task<Func<Task<TransportMessage>>> SendAndReceive(TransportMessage message)
        {
            throw new NotImplementedException();
        }

        public override Task<Func<Task<TransportMessage>>> SendAndReceive(TransportMessage message, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }

    internal static class MessageHelpers
    {
        public static StringContent ToStringContent(this TransportMessage message)
        {
            var contentBuilder = new StringBuilder();
            contentBuilder.Append("{");
            contentBuilder.Append($"\"payload\": \"{Convert.ToBase64String(message.Payload)}\",");
            contentBuilder.Append("\"metadata\": [");
            for (var a = 0; a < message.Metadata.Frames.Count; a++)
            {
                contentBuilder.Append($"{{\"id\": \"{message.Metadata.Frames[a].Id}\",\"data\": \"{Convert.ToBase64String(message.Metadata.Frames[a].Data)}\"}}");
                if (a >= message.Metadata.Frames.Count - 1)
                    contentBuilder.Append(",");
            }
            contentBuilder.Append("]");
            contentBuilder.Append("}");

            return new StringContent(contentBuilder.ToString(), Encoding.UTF8, "application/json");
        }

        public static void WriteTransportMessage(this BinaryWriter writer, TransportMessage message)
        {
            writer.Write(0);

            writer.Write(message.Metadata.Frames.Count);

            foreach (var frame in message.Metadata.Frames)
            {
                var encodedId = Encoding.UTF8.GetBytes(frame.Id);
                writer.Write(encodedId.Length);
                writer.Write(encodedId);

                writer.Write(frame.Data.Length);
                writer.Write(frame.Data);
            }

            writer.Write(message.Payload.Length);
            writer.Write(message.Payload);
        }

        public static TransportMessage ReadTransportMessage(this BinaryReader reader)
        {
            var metadata = new VolatileTransportMetadata();

            var signal = reader.ReadInt32();
            if (signal != 0)
                throw new Exception("Message received with signal code " + signal.ToString());

            var frameCount = reader.ReadInt32();
            for (var a = 0; a < frameCount; a++)
            {
                var idLength = reader.ReadInt32();
                var encodedId = reader.ReadBytes(idLength);

                var dataLength = reader.ReadInt32();
                var data = reader.ReadBytes(dataLength);

                metadata.Frames.Add(new VolatileTransportMetadataFrame(Encoding.UTF8.GetString(encodedId), data));
            }

            var payloadLength = reader.ReadInt32();
            var payload = reader.ReadBytes(payloadLength);

            return new TransportMessage(payload, metadata);
        }
    }
}

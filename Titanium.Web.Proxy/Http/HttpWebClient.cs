using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Used to communicate with the server over HTTP(S)
    /// </summary>
    public class HttpWebClient : IDisposable
    {
        /// <summary>
        /// Connection to server
        /// </summary>
        internal TcpConnection ServerConnection { get; set; }

        /// <summary>
        /// Request ID.
        /// </summary>
        public Guid RequestId { get; }

        /// <summary>
        /// Headers passed with Connect.
        /// </summary>
        public ConnectRequest ConnectRequest { get; set; }

        /// <summary>
        /// Web Request.
        /// </summary>
        public Request Request { get; set; }

        /// <summary>
        /// Web Response.
        /// </summary>
        public Response Response { get; set; }

        /// <summary>
        /// PID of the process that is created the current session when client is running in this machine
        /// If client is remote then this will return 
        /// </summary>
        public Lazy<int> ProcessId { get; internal set; }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps => Request.RequestUri.Scheme == ProxyServer.UriSchemeHttps;


        internal HttpWebClient()
        {
            RequestId = Guid.NewGuid();
            Request = new Request();
            Response = new Response();
        }

        /// <summary>
        /// Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="connection">Instance of <see cref="TcpConnection"/></param>
        internal void SetConnection(TcpConnection connection)
        {
            connection.LastAccess = DateTime.Now;
            ServerConnection = connection;
        }


        /// <summary>
        /// Prepare and send the http(s) request
        /// </summary>
        /// <returns></returns>
        internal async Task SendRequest(bool enable100ContinueBehaviour)
        {
            var stream = ServerConnection.Stream;

            var requestLines = new StringBuilder();

            //prepare the request & headers
            requestLines.AppendLine($"{Request.Method} {Request.RequestUri.PathAndQuery} HTTP/{Request.HttpVersion.Major}.{Request.HttpVersion.Minor}");


            //Send Authentication to Upstream proxy if needed
            if (ServerConnection.UpStreamHttpProxy != null
                && ServerConnection.IsHttps == false
                && !string.IsNullOrEmpty(ServerConnection.UpStreamHttpProxy.UserName)
                && ServerConnection.UpStreamHttpProxy.Password != null)
            {
                requestLines.AppendLine("Proxy-Connection: keep-alive");
                requestLines.AppendLine("Proxy-Authorization" + ": Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                            $"{ServerConnection.UpStreamHttpProxy.UserName}:{ServerConnection.UpStreamHttpProxy.Password}")));
            }
            //write request headers
            foreach (var header in Request.RequestHeaders)
            {
                if (header.Name != "Proxy-Authorization")
                {
                    requestLines.AppendLine($"{header.Name}: {header.Value}");
                }
            }

            requestLines.AppendLine();

            string request = requestLines.ToString();
            var requestBytes = Encoding.ASCII.GetBytes(request);

            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await stream.FlushAsync();

            if (enable100ContinueBehaviour)
            {
                if (Request.ExpectContinue)
                {
                    var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
                    string responseStatusCode = httpResult[1].Trim();
                    string responseStatusDescription = httpResult[2].Trim();

                    //find if server is willing for expect continue
                    if (responseStatusCode.Equals("100")
                        && responseStatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.Is100Continue = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                    else if (responseStatusCode.Equals("417")
                             && responseStatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.ExpectationFailed = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Receive and parse the http response from server
        /// </summary>
        /// <returns></returns>
        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (Response.ResponseStatusCode != null)
                return;

            string line = await ServerConnection.StreamReader.ReadLineAsync();
            if (line == null)
            {
                throw new IOException();
            }

            var httpResult = line.Split(ProxyConstants.SpaceSplit, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
            {
                //Empty content in first-line, try again
                httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
            }

            string httpVersion = httpResult[0];

            var version = HttpHeader.Version11;
            if (string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase))
            {
                version = HttpHeader.Version10;
            }

            Response.HttpVersion = version;
            Response.ResponseStatusCode = httpResult[1].Trim();
            Response.ResponseStatusDescription = httpResult[2].Trim();

            //For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (Response.ResponseStatusCode.Equals("100")
                && Response.ResponseStatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
            {
                //Read the next line after 100-continue 
                Response.Is100Continue = true;
                Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response
                await ReceiveResponse();
                return;
            }

            if (Response.ResponseStatusCode.Equals("417")
                && Response.ResponseStatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
            {
                //read next line after expectation failed response
                Response.ExpectationFailed = true;
                Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response 
                await ReceiveResponse();
                return;
            }

            //Read the response headers in to unique and non-unique header collections
            await HeaderParser.ReadHeaders(ServerConnection.StreamReader, Response.ResponseHeaders);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            ConnectRequest = null;

            Request.Dispose();
            Response.Dispose();
        }
    }
}

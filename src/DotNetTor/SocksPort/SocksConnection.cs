﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetTor.SocksPort
{
	internal sealed class SocksConnection
	{
		public IPEndPoint EndPoint = null;
		public Uri Destination;
		public Socket Socket;
		public Stream Stream;
		public int ReferenceCount;
		private object _lock = new object();

		private void HandshakeTor()
		{
			var sendBuffer = new byte[] { 5, 1, 0 };
			Socket.Send(sendBuffer, SocketFlags.None);

			var recBuffer = new byte[Socket.ReceiveBufferSize];
			var recCnt = Socket.Receive(recBuffer, SocketFlags.None);

			Util.ValidateHandshakeResponse(recBuffer, recCnt);
		}


		private void ConnectSocket()
		{
			Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
			{
				Blocking = true
			};
			Socket.Connect(EndPoint);
		}

		private void ConnectToDestination(bool ignoreSslCertification = false)
		{
			var sendBuffer = Util.BuildConnectToUri(Destination).Array;
			Socket.Send(sendBuffer, SocketFlags.None);

			var recBuffer = new byte[Socket.ReceiveBufferSize];
			var recCnt = Socket.Receive(recBuffer, SocketFlags.None);

			Util.ValidateConnectToDestinationResponse(recBuffer, recCnt);

			Stream stream = new NetworkStream(Socket, ownsSocket: false);
			if (Destination.Scheme.Equals("https", StringComparison.Ordinal))
			{
				SslStream httpsStream;
				if(ignoreSslCertification)
				{
					httpsStream = new SslStream(
						stream,
						leaveInnerStreamOpen: true,
						userCertificateValidationCallback: (a, b, c, d) => true);
				}
				else
				{
					httpsStream = new SslStream(stream, leaveInnerStreamOpen: true);
				}

				httpsStream
					.AuthenticateAsClientAsync(
						Destination.DnsSafeHost,
						new X509CertificateCollection(),
						SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
						checkCertificateRevocation: true)
					.Wait();
				stream = httpsStream;
			}
			Stream = stream;
		}

		private bool IsSocketConnected(bool throws)
		{
			try
			{
				if (Socket == null)
					return false;
				if (!Socket.Connected)
					return false;
				//if (Socket.Available == 0)
				//	return false;
				//if (Socket.Poll(1000, SelectMode.SelectRead))
				//	return false;

				return true;
			}
			catch
			{
				if (throws)
					throw;

				return false;
			}
		}

		public async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ctsToken, bool ignoreSslCertification = false)
		{
			try
			{
				EnsureConnectedToTor(ignoreSslCertification);
				ctsToken.ThrowIfCancellationRequested();

				// https://tools.ietf.org/html/rfc7230#section-3.3.2
				// A user agent SHOULD send a Content - Length in a request message when
				// no Transfer-Encoding is sent and the request method defines a meaning
				// for an enclosed payload body.For example, a Content - Length header
				// field is normally sent in a POST request even when the value is 0
				// (indicating an empty payload body).A user agent SHOULD NOT send a
				// Content - Length header field when the request message does not contain
				// a payload body and the method semantics do not anticipate such a
				// body.
				// TODO implement it fully (altough probably .NET already ensures it)
				if (request.Method == HttpMethod.Post)
				{
					if (request.Headers.TransferEncoding.Count == 0)
					{
						if (request.Content == null)
						{
							request.Content = new ByteArrayContent(new byte[] { }); // dummy empty content
							request.Content.Headers.ContentLength = 0;
						}
						else
						{
							if (request.Content.Headers.ContentLength == null)
							{
								request.Content.Headers.ContentLength = (await request.Content.ReadAsStringAsync().ConfigureAwait(false)).Length;
							}
						}
					}
				}			

				var requestString = await request.ToHttpStringAsync(ctsToken).ConfigureAwait(false);
				ctsToken.ThrowIfCancellationRequested();

				Stream.Write(Encoding.UTF8.GetBytes(requestString), 0, requestString.Length);
				Stream.Flush();
				ctsToken.ThrowIfCancellationRequested();

				return await new HttpResponseMessage().CreateNewAsync(Stream, request.Method, ctsToken).ConfigureAwait(false);
			}
			catch (SocketException)
			{
				DestroySocket();
				throw;
			}
		}

		private void EnsureConnectedToTor(bool ignoreSslCertification)
		{
			if (!IsSocketConnected(throws: false)) // Socket.Connected is misleading, don't use that
			{
				DestroySocket();
				ConnectSocket();
				HandshakeTor();
				ConnectToDestination(ignoreSslCertification);
			}
		}

		public void AddReference() => Interlocked.Increment(ref ReferenceCount);

		public void RemoveReference(out bool disposed)
		{
			disposed = false;
			var value = Interlocked.Decrement(ref ReferenceCount);
			if (value == 0)
			{
				lock (_lock)
				{
					DestroySocket();
					disposed = true;
				}
			}
		}

		private void DestroySocket()
		{
			if (Stream != null)
			{
				Stream.Dispose();
				Stream = null;
			}
			if (Socket != null)
			{
				try
				{
					Socket.Shutdown(SocketShutdown.Both);
				}
				catch (SocketException) { }
				Socket.Dispose();
				Socket = null;
			}
		}
	}
}

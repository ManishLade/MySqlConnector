﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using MySql.Data.Serialization;

namespace MySqlConnector.Tests
{
	internal sealed class FakeMySqlServerConnection
	{
		public FakeMySqlServerConnection(FakeMySqlServer server, int connectionId)
		{
			m_server = server ?? throw new ArgumentNullException(nameof(server));
			m_connectionId = connectionId;
		}

		public async Task RunAsync(TcpClient client, CancellationToken token)
		{
			try
			{
				using (token.Register(client.Dispose))
				using (client)
				using (var stream = client.GetStream())
				{
					await SendAsync(stream, 0, WriteInitialHandshake);
					await ReadPayloadAsync(stream, token); // handshake response
					await SendAsync(stream, 2, WriteOk);

					var keepRunning = true;
					while (keepRunning)
					{
						var bytes = await ReadPayloadAsync(stream, token);
						switch ((CommandKind) bytes[0])
						{
						case CommandKind.Quit:
							await SendAsync(stream, 1, WriteOk);
							keepRunning = false;
							break;

						case CommandKind.Ping:
						case CommandKind.Query:
						case CommandKind.ResetConnection:
								await SendAsync(stream, 1, WriteOk);
							break;

						default:
							Console.WriteLine("** UNHANDLED ** {0}", (CommandKind) bytes[0]);
							await SendAsync(stream, 1, WriteError);
							break;
						}
					}
				}
			}
			finally
			{
				m_server.ClientDisconnected();
			}
		}

		private static async Task SendAsync(Stream stream, int sequenceNumber, Action<BinaryWriter> writePayload)
		{
			var packet = MakePayload(sequenceNumber, writePayload);
			await stream.WriteAsync(packet, 0, packet.Length);
		}

		private static byte[] MakePayload(int sequenceNumber, Action<BinaryWriter> writePayload)
		{
			using (var memoryStream = new MemoryStream())
			{
				using (var writer = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
				{
					writer.Write(default(int));
					writePayload(writer);
					memoryStream.Position = 0;
					writer.Write(((int) (memoryStream.Length - 4)) | ((sequenceNumber % 256) << 24));
				}
				return memoryStream.ToArray();
			}
		}

		private static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken token)
		{
			var header = await ReadBytesAsync(stream, 4, token);
			var length = header[0] | (header[1] << 8) | (header[2] << 16);
			var sequenceNumber = header[3];
			return await ReadBytesAsync(stream, length, token);
		}

		private static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken token)
		{
			var bytes = new byte[count];
			for (var bytesRead = 0; bytesRead < count;)
				bytesRead += await stream.ReadAsync(bytes, bytesRead, count - bytesRead, token);
			return bytes;
		}

		private void WriteInitialHandshake(BinaryWriter writer)
		{
			var random = new Random(1);
			var authData = new byte[20];
			random.NextBytes(authData);
			var capabilities =
				ProtocolCapabilities.LongPassword |
				ProtocolCapabilities.FoundRows |
				ProtocolCapabilities.LongFlag |
				ProtocolCapabilities.IgnoreSpace |
				ProtocolCapabilities.Protocol41 |
				ProtocolCapabilities.Transactions |
				ProtocolCapabilities.SecureConnection |
				ProtocolCapabilities.MultiStatements |
				ProtocolCapabilities.MultiResults |
				ProtocolCapabilities.PluginAuth |
				ProtocolCapabilities.ConnectionAttributes |
				ProtocolCapabilities.PluginAuthLengthEncodedClientData;

			writer.Write((byte) 10); // protocol version
			writer.WriteNullTerminated(m_server.ServerVersion); // server version
			writer.Write(m_connectionId); // conection ID
			writer.Write(authData, 0, 8); // auth plugin data part 1
			writer.Write((byte) 0); // filler
			writer.Write((ushort) capabilities);
			writer.Write((byte) CharacterSet.Utf8Binary); // character set
			writer.Write((ushort) 0); // status flags
			writer.Write((ushort) ((uint) capabilities >> 16));
			writer.Write((byte) authData.Length);
			writer.Write(new byte[10]); // reserved
			writer.Write(authData, 8, authData.Length - 8);
			if (authData.Length - 8 < 13)
				writer.Write(new byte[13 - (authData.Length - 8)]); // have to write at least 13 bytes
			writer.WriteNullTerminated("mysql_native_password");
		}

		private static void WriteOk(BinaryWriter writer)
		{
			writer.Write((byte) 0); // signature
			writer.Write((byte) 0); // 0 rows affected
			writer.Write((byte) 0); // last insert ID
			writer.Write((ushort) 0); // server status
			writer.Write((ushort) 0); // warning count
		}

		private static void WriteError(BinaryWriter writer)
		{
			writer.Write((byte) 0xFF); // signature
			writer.Write((ushort) MySqlErrorCode.UnknownError); // error code
			writer.WriteRaw("#ERROR");
			writer.WriteRaw("An unknown error occurred");
		}

		readonly FakeMySqlServer m_server;
		readonly int m_connectionId;
	}
}
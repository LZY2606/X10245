using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace CacheTower.Tests.Utils;

public static class RedisHelper
{
	public static string Endpoint => Environment.GetEnvironmentVariable("REDIS_ENDPOINT") ?? "localhost:6379";

	private static readonly ConcurrentQueue<string> Errors = new();

	private static ConnectionMultiplexer Connection { get; set; }

	/// <summary>
	/// Marks the current test as inconclusive when no Redis server is reachable at <see cref="Endpoint"/>.
	/// This keeps the test suite runnable in environments without external services while preserving the tests.
	/// </summary>
	public static void ThrowIfUnavailable()
	{
		var separatorIndex = Endpoint.LastIndexOf(':');
		var host = separatorIndex > 0 ? Endpoint[..separatorIndex] : Endpoint;
		var port = separatorIndex > 0 && int.TryParse(Endpoint[(separatorIndex + 1)..], out var parsedPort) ? parsedPort : 6379;
		try
		{
			using var client = new TcpClient();
			var connectTask = client.ConnectAsync(host, port);
			if (!connectTask.Wait(TimeSpan.FromSeconds(1)))
			{
				throw new TimeoutException("Timed out connecting to Redis");
			}
		}
		catch (Exception ex)
		{
			Assert.Inconclusive($"Redis is not available at {Endpoint}: {ex.Message}");
		}
	}

	public static ConnectionMultiplexer GetConnection()
	{
		ThrowIfUnavailable();
		if (Connection == null)
		{
			var config = new ConfigurationOptions
			{
				AllowAdmin = true
			};
			config.EndPoints.Add(Endpoint);
			Connection = ConnectionMultiplexer.Connect(config);
			Connection.ErrorMessage += (sender, args) =>
			{
				Errors.Enqueue(args.Message);
			};
			Connection.InternalError += (sender, args) =>
			{
				if (args.Exception is not null)
				{
					Errors.Enqueue(args.Exception.Message);
				}
			};
		}
		return Connection;
	}

	/// <summary>
	/// Flushes Redis and resets the state of error logging
	/// </summary>
	public static void ResetState()
	{
		GetConnection().GetServer(Endpoint).FlushDatabase();
		GetConnection().GetSubscriber().UnsubscribeAll();
		
		//.NET Framework doesn't support `Clear()` on Errors so we do it manually
		while (!Errors.IsEmpty)
		{
			Errors.TryDequeue(out _);
		}
	}

	public static void DebugInfo(IConnectionMultiplexer connection)
	{
		Debug.WriteLine("== Redis Connection Status ==");
		Debug.WriteLine(connection.GetStatus());

		Debug.WriteLine("== Errors (Redis and Internal) ==");
		while (!Errors.IsEmpty)
		{
			if (Errors.TryDequeue(out var message))
			{
				Debug.WriteLine(message);
			}
		}
	}
}

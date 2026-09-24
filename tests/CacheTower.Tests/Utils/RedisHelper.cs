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

	/// <summary>
	/// Whether a Redis server is reachable at <see cref="Endpoint"/>. Probed once, lazily.
	/// </summary>
	public static bool IsAvailable { get; } = ProbeAvailability();

	private static bool ProbeAvailability()
	{
		try
		{
			var firstEndpoint = Endpoint.Split(',')[0].Trim();
			var hostPort = firstEndpoint.Split(':');
			var host = hostPort[0];
			var port = hostPort.Length > 1 ? int.Parse(hostPort[1]) : 6379;
			using var client = new TcpClient();
			var connectTask = client.ConnectAsync(host, port);
			return connectTask.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Marks the current test as inconclusive when no Redis server is available, rather than failing on a missing service.
	/// </summary>
	public static void ThrowIfUnavailable()
	{
		if (!IsAvailable)
		{
			Assert.Inconclusive($"Redis is not available at '{Endpoint}'; integration tests that require it are skipped as inconclusive.");
		}
	}

	private static readonly ConcurrentQueue<string> Errors = new();

	static RedisHelper()
	{
		if (!IsAvailable)
		{
			return;
		}

		var connection = GetConnection();
		connection.ErrorMessage += (sender, args) =>
		{
			Errors.Enqueue(args.Message);
		};
		connection.InternalError += (sender, args) =>
		{
			if (args.Exception is not null)
			{
				Errors.Enqueue(args.Exception.Message);
			}
		};
	}

	private static ConnectionMultiplexer Connection { get; set; }

	public static ConnectionMultiplexer GetConnection()
	{
		if (Connection == null)
		{
			var config = new ConfigurationOptions
			{
				AllowAdmin = true
			};
			config.EndPoints.Add(Endpoint);
			Connection = ConnectionMultiplexer.Connect(config);
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

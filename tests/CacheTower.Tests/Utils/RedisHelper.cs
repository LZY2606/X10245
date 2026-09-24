using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace CacheTower.Tests.Utils;

public static class RedisHelper
{
	public static string Endpoint => Environment.GetEnvironmentVariable("REDIS_ENDPOINT") ?? "localhost:6379";

	private static readonly ConcurrentQueue<string> Errors = new();
	private static readonly Lazy<bool> Availability = new(ProbeAvailability);

	private static ConnectionMultiplexer Connection { get; set; }

	/// <summary>
	/// Whether a Redis server is reachable at <see cref="Endpoint"/>. Probed once with a short timeout.
	/// </summary>
	public static bool IsAvailable => Availability.Value;

	private static bool ProbeAvailability()
	{
		try
		{
			var config = new ConfigurationOptions
			{
				ConnectTimeout = 1000,
				ConnectRetry = 0,
				AbortOnConnectFail = false
			};
			config.EndPoints.Add(Endpoint);
			using var probe = ConnectionMultiplexer.Connect(config);
			return probe.IsConnected;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Marks the current test as inconclusive when no Redis server is available, allowing
	/// the test suite to run on machines without a Redis service.
	/// </summary>
	public static void ThrowIfUnavailable()
	{
		if (!IsAvailable)
		{
			Assert.Inconclusive($"Redis is not available at {Endpoint}; set the REDIS_ENDPOINT environment variable to run this test.");
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
		ThrowIfUnavailable();
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

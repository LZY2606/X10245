using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using StackExchange.Redis;

namespace CacheTower.Tests.Utils;

public static class RedisHelper
{
	public static string Endpoint => Environment.GetEnvironmentVariable("REDIS_ENDPOINT") ?? "localhost:6379";

	private static readonly ConcurrentQueue<string> Errors = new();

	private static readonly Lazy<bool> IsAvailableLazy = new(() =>
	{
		try
		{
			using var connection = ConnectionMultiplexer.Connect(new ConfigurationOptions
			{
				AllowAdmin = true,
				ConnectTimeout = 1000,
				AbortOnConnectFail = true,
				EndPoints = { Endpoint }
			});
			return connection.IsConnected;
		}
		catch
		{
			return false;
		}
	});

	/// <summary>
	/// Whether a Redis server is reachable. Tests that require a live server should be marked inconclusive when this is false.
	/// </summary>
	public static bool IsAvailable => IsAvailableLazy.Value;

	private static ConnectionMultiplexer Connection { get; set; }

	public static ConnectionMultiplexer GetConnection()
	{
		if (Connection == null)
		{
			var config = new ConfigurationOptions
			{
				AllowAdmin = true,
				ConnectTimeout = 1000,
				AbortOnConnectFail = false
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

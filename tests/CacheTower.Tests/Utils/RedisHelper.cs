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

	private static ConnectionMultiplexer Connection { get; set; }

	public static ConnectionMultiplexer GetConnection()
	{
		if (Connection == null)
		{
			var config = new ConfigurationOptions
			{
				AllowAdmin = true,
				//Allow the connection object to be created even when no Redis server is reachable so
				//tests that never issue commands still run; tests that need a live server call ResetState().
				AbortOnConnectFail = false,
				ConnectTimeout = 1000,
				ConnectRetry = 0,
				SyncTimeout = 1000
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
	/// Checks whether a Redis server is reachable at <see cref="Endpoint"/>.
	/// </summary>
	public static bool IsAvailable()
	{
		try
		{
			return GetConnection().GetDatabase().Ping() >= TimeSpan.Zero;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Flushes Redis and resets the state of error logging
	/// </summary>
	public static void ResetState()
	{
		if (!IsAvailable())
		{
			Assert.Inconclusive($"Redis is not available at '{Endpoint}'; skipping test that requires a live Redis server.");
		}

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

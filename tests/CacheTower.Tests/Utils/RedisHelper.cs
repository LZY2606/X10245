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
	private static bool? IsEndpointAvailable;

	static RedisHelper()
	{
		try
		{
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
		catch
		{
			//Redis is unavailable; tests that require it are marked as inconclusive instead of failing.
		}
	}

	private static ConnectionMultiplexer Connection { get; set; }

	/// <summary>
	/// Whether a Redis server can be reached at <see cref="Endpoint"/>. Probed once and cached.
	/// </summary>
	public static bool IsAvailable
	{
		get
		{
			if (IsEndpointAvailable is null)
			{
				try
				{
					Connection ??= Connect();
					IsEndpointAvailable = true;
				}
				catch
				{
					IsEndpointAvailable = false;
				}
			}
			return IsEndpointAvailable.Value;
		}
	}

	/// <summary>
	/// Marks the current test as inconclusive when Redis is unavailable and no explicit endpoint was configured.
	/// </summary>
	public static void ThrowIfUnavailable()
	{
		if (!IsAvailable && Environment.GetEnvironmentVariable("REDIS_ENDPOINT") is null)
		{
			Assert.Inconclusive($"Redis is not available at {Endpoint}; skipping integration test.");
		}
	}

	private static ConnectionMultiplexer Connect()
	{
		var config = new ConfigurationOptions
		{
			AllowAdmin = true,
			ConnectTimeout = 2000
		};
		config.EndPoints.Add(Endpoint);
		return ConnectionMultiplexer.Connect(config);
	}

	public static ConnectionMultiplexer GetConnection()
	{
		ThrowIfUnavailable();
		if (Connection == null)
		{
			Connection = Connect();
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

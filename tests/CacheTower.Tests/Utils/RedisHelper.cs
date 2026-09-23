using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using StackExchange.Redis;

namespace CacheTower.Tests.Utils;

public static class RedisHelper
{
	public static string Endpoint => Environment.GetEnvironmentVariable("REDIS_ENDPOINT") ?? "localhost:6379";

	private static readonly ConcurrentQueue<string> Errors = new();

	private static bool? Available;

	/// <summary>
	/// Probes whether a Redis server is reachable at <see cref="Endpoint"/> so integration tests can be marked inconclusive instead of failing when no server is running.
	/// </summary>
	public static bool IsAvailable()
	{
		Available ??= Probe();
		return Available.Value;

		static bool Probe()
		{
			try
			{
				var separatorIndex = Endpoint.LastIndexOf(':');
				var host = separatorIndex > 0 ? Endpoint[..separatorIndex] : Endpoint;
				var port = separatorIndex > 0 ? int.Parse(Endpoint[(separatorIndex + 1)..]) : 6379;
				using var client = new TcpClient();
				return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
			}
			catch
			{
				return false;
			}
		}
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

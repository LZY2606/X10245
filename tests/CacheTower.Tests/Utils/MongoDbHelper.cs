using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		/// <summary>
		/// Whether a MongoDB server is reachable at <see cref="ConnectionString"/>. Probed once, lazily.
		/// </summary>
		public static bool IsAvailable { get; } = ProbeAvailability();

		private static bool ProbeAvailability()
		{
			try
			{
				var url = new MongoUrl(ConnectionString);
				var server = url.GetServers().First();
				using var client = new TcpClient();
				var connectTask = client.ConnectAsync(server.Host, server.Port);
				return connectTask.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Marks the current test as inconclusive when no MongoDB server is available, rather than failing on a missing service.
		/// </summary>
		public static void ThrowIfUnavailable()
		{
			if (!IsAvailable)
			{
				Assert.Inconclusive($"MongoDB is not available at '{ConnectionString}'; integration tests that require it are skipped as inconclusive.");
			}
		}

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		public static IMongoDbConnection GetConnection()
		{
			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
		}

		public static async Task DropDatabaseAsync()
		{
			await GetConnection().Client.DropDatabaseAsync(GetDatabaseName());
		}
		public static void DropDatabase()
		{
			GetConnection().Client.DropDatabase(GetDatabaseName());
		}
	}
}

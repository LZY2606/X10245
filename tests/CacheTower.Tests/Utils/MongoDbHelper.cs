using System;
using System.Collections.Generic;
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

		public static string GetDatabaseName()
		{
			return "CacheTowerTests";
		}

		/// <summary>
		/// Marks the current test as inconclusive when no MongoDB server is reachable.
		/// This keeps the test suite runnable in environments without external services while preserving the tests.
		/// </summary>
		public static void ThrowIfUnavailable()
		{
			var url = new MongoUrl(ConnectionString);
			var host = url.Server.Host;
			var port = url.Server.Port;
			try
			{
				using var client = new TcpClient();
				var connectTask = client.ConnectAsync(host, port);
				if (!connectTask.Wait(TimeSpan.FromSeconds(1)))
				{
					throw new TimeoutException("Timed out connecting to MongoDB");
				}
			}
			catch (Exception ex)
			{
				Assert.Inconclusive($"MongoDB is not available at {host}:{port}: {ex.Message}");
			}
		}

		public static IMongoDbConnection GetConnection()
		{
			ThrowIfUnavailable();
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

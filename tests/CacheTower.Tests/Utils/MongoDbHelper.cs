using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		private static bool? Available;

		/// <summary>
		/// Probes whether a MongoDB server is reachable so integration tests can be marked inconclusive instead of failing when no server is running.
		/// </summary>
		public static bool IsAvailable()
		{
			Available ??= Probe();
			return Available.Value;

			static bool Probe()
			{
				try
				{
					var host = new MongoUrl(ConnectionString).Server;
					using var client = new TcpClient();
					return client.ConnectAsync(host.Host, host.Port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
				}
				catch
				{
					return false;
				}
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

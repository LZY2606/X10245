using System;
using System.Collections.Generic;
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

		public static IMongoDbConnection GetConnection()
		{
			var urlBuilder = new MongoUrlBuilder(ConnectionString)
			{
				DatabaseName = GetDatabaseName()
			};
			return MongoDbConnection.FromUrl(urlBuilder.ToMongoUrl());
		}

		/// <summary>
		/// Checks whether a MongoDB server is reachable at <see cref="ConnectionString"/>.
		/// </summary>
		public static async Task<bool> IsAvailableAsync()
		{
			try
			{
				var settings = MongoClientSettings.FromUrl(new MongoUrl(ConnectionString));
				settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
				settings.ConnectTimeout = TimeSpan.FromSeconds(1);
				var client = new MongoClient(settings);
				using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
				await client.ListDatabaseNamesAsync(cancellation.Token);
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static async Task DropDatabaseAsync()
		{
			if (!await IsAvailableAsync())
			{
				Assert.Inconclusive($"MongoDB is not available at '{ConnectionString}'; skipping test that requires a live MongoDB server.");
			}

			await GetConnection().Client.DropDatabaseAsync(GetDatabaseName());
		}
		public static void DropDatabase()
		{
			GetConnection().Client.DropDatabase(GetDatabaseName());
		}
	}
}

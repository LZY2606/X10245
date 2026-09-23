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
		/// Whether a MongoDB server is reachable at <see cref="ConnectionString"/>.
		/// </summary>
		public static bool IsAvailable()
		{
			try
			{
				var settings = MongoClientSettings.FromConnectionString(ConnectionString);
				settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
				var client = new MongoClient(settings);
				using (client.ListDatabaseNames())
				{
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Marks the current test as inconclusive when no MongoDB server is reachable, so the test suite does not require a running service.
		/// </summary>
		public static void RequireAvailable()
		{
			if (!IsAvailable())
			{
				Assert.Inconclusive($"MongoDB is not available at {ConnectionString}. Start MongoDB or set MONGODB_URI to run this test.");
			}
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

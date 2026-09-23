using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoFramework;

namespace CacheTower.Tests.Utils
{
	public static class MongoDbHelper
	{
		public static string ConnectionString => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost";

		private static readonly Lazy<bool> IsAvailableLazy = new(() =>
		{
			try
			{
				var urlBuilder = new MongoUrlBuilder(ConnectionString)
				{
					ServerSelectionTimeout = TimeSpan.FromSeconds(1)
				};
				var client = new MongoClient(urlBuilder.ToMongoUrl());
				using var cursor = client.ListDatabaseNames();
				return cursor.MoveNext();
			}
			catch
			{
				return false;
			}
		});

		/// <summary>
		/// Whether a MongoDB server is reachable. Tests that require a live server should be marked inconclusive when this is false.
		/// </summary>
		public static bool IsAvailable => IsAvailableLazy.Value;

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
